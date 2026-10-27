#Requires -Version 5.1
<#
.SYNOPSIS
  Send one test message with SMTP, Microsoft Graph or the Cloudflare REST API, and obtain OAuth2
  refresh tokens to hand to LMail.
.DESCRIPTION
  Single file: copy it anywhere. It reads mailtest.config.yaml from its own directory and creates
  that file from an embedded template when it is missing (then stops without sending).
  SMTP uses .NET TcpClient + SslStream with AUTH LOGIN / PLAIN / XOAUTH2.
  Two entries: send a test mail (default; uses default_profile or -Profile), or -InitOAuth to
  authorize an OAuth2 provider. With no arguments in a terminal a menu offers both. -InitOAuth lists
  the OAuth2 profiles to choose from, signs the user in through a local web server on this machine
  (or the Microsoft device code flow), prints the refresh token and exits without sending. The
  authorization URL is only printed; the tool never opens a browser.
.EXAMPLE
  .\mailtest.ps1
.EXAMPLE
  .\mailtest.ps1 -Profile smtp_ssl
.EXAMPLE
  .\mailtest.ps1 -Profile gmail_oauth -InitOAuth
#>
[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'mailtest.config.yaml'),
    [Alias('Profile')][string]$ProfileName,
    [string[]]$To,
    [string]$Subject,
    [string]$Body,
    [switch]$InitOAuth,
    [switch]$ValidateOnly,
    [switch]$VerboseProtocol
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:TraceSmtp = [bool]$VerboseProtocol
if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

function Get-Setting {
    param([AllowNull()][object]$Map, [string]$Key, [AllowNull()][object]$Default = $null)
    if ($null -eq $Map) { return $Default }
    if ($Map -is [System.Collections.IDictionary]) {
        if ($Map.Contains($Key)) { return $Map[$Key] }
        return $Default
    }
    $prop = $Map.PSObject.Properties[$Key]
    if ($null -ne $prop) { return $prop.Value }
    return $Default
}

function Require-String {
    param([object]$Map, [string]$Key, [string]$Context)
    $value = Get-Setting $Map $Key
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        throw "Missing '$Context.$Key' in YAML."
    }
    return [string]$value
}

function Resolve-Secret {
    param([AllowNull()][object]$Value, [string]$Name)
    $s = [string]$Value
    if ([string]::IsNullOrWhiteSpace($s)) { throw "Missing credential: $Name" }
    if ($s -match '^env:([A-Za-z_][A-Za-z0-9_]*)$') {
        $envName = $Matches[1]
        $resolved = [Environment]::GetEnvironmentVariable($envName)
        if ([string]::IsNullOrEmpty($resolved)) { throw "Environment variable '$envName' is not set (required by $Name)." }
        return $resolved
    }
    return $s
}

function Test-Windows {
    return ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT)
}

function Check-MailAddress {
    param([string]$Address, [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Address) -or $Address -match '[\r\n]') {
        throw "Invalid email address in $Name."
    }
    try { $parsed = [Net.Mail.MailAddress]::new($Address) }
    catch { throw "Invalid email address in ${Name}: $Address" }
    if ($parsed.Address -ne $Address -or $Address -notmatch '^[\x21-\x7e]+$') {
        throw "Use a plain ASCII email address (without display name) for ${Name}: $Address"
    }
    return $Address
}

function Get-MailSettings {
    param([object]$Settings, [string[]]$OverrideTo = @())
    $sender = Check-MailAddress (Require-String $Settings 'from' 'message') 'message.from'
    $recipientsRaw = if ($OverrideTo.Count -gt 0) { $OverrideTo } else { Get-Setting $Settings 'to' }
    $recipients = @()
    foreach ($item in @($recipientsRaw)) {
        if ($null -eq $item) { continue }
        $recipients += (Check-MailAddress ([string]$item) 'message.to')
    }
    if ($recipients.Count -eq 0) { throw 'At least one recipient is required (message.to or -To).' }
    $cc = @()
    foreach ($item in @(Get-Setting $Settings 'cc')) {
        if ($null -ne $item -and "${item}" -ne '') { $cc += (Check-MailAddress ([string]$item) 'message.cc') }
    }
    $bcc = @()
    foreach ($item in @(Get-Setting $Settings 'bcc')) {
        if ($null -ne $item -and "${item}" -ne '') { $bcc += (Check-MailAddress ([string]$item) 'message.bcc') }
    }
    $subjectValue = [string](Get-Setting $Settings 'subject' '[SMTP Test] Connectivity check')
    Assert-MailSubject $subjectValue
    $textValue = [string](Get-Setting $Settings 'text' 'This is a test email.')
    $htmlValue = [string](Get-Setting $Settings 'html' '')
    if ([string]::IsNullOrEmpty($textValue) -and [string]::IsNullOrEmpty($htmlValue)) {
        throw 'At least one of message.text or message.html is required.'
    }
    return @{ from = $sender; to = $recipients; cc = $cc; bcc = $bcc; subject = $subjectValue; text = $textValue; html = $htmlValue }
}

function Assert-MailSubject {
    param([AllowEmptyString()][string]$Value)
    # Raw control characters are illegal in mail headers; Unicode text is MIME encoded.
    if ($Value -match '[\x00-\x1F\x7F]') {
        throw 'Email subject must not contain control characters or line breaks.'
    }
}

function Test-OAuthSettings {
    param([object]$OAuth, [ValidateSet('smtp','graph')][string]$Target)
    if ($null -eq $OAuth -or $OAuth -isnot [System.Collections.IDictionary]) {
        throw 'oauth2 must be a YAML mapping.'
    }
    $provider = [string](Get-Setting $OAuth 'provider' 'microsoft')
    $grant = [string](Get-Setting $OAuth 'grant' 'refresh_token')
    if ($provider -notin @('microsoft','google','generic')) { throw "Unsupported oauth2.provider: $provider" }
    if ($Target -eq 'graph' -and $provider -ne 'microsoft') { throw 'Microsoft Graph requires oauth2.provider: microsoft.' }
    if ($grant -eq 'delegated') { throw 'oauth2.grant: delegated was removed; use grant: refresh_token (see docs/MAILTEST.md).' }
    if ($grant -notin @('access_token','client_credentials','refresh_token')) {
        throw "Unsupported oauth2.grant: $grant"
    }
    if ($grant -eq 'access_token') {
        [void](Require-String $OAuth 'access_token' 'oauth2')
        return
    }
    [void](Require-String $OAuth 'client_id' 'oauth2')
    $tenant = ''
    if ($provider -eq 'microsoft') {
        $tenant = [string](Get-Setting $OAuth 'tenant' 'organizations')
        if ($tenant -notmatch '^[A-Za-z0-9.-]+$') { throw 'Invalid oauth2.tenant.' }
    }
    $flow = [string](Get-Setting $OAuth 'flow' 'loopback')
    if ($flow -notin @('loopback','device_code')) { throw 'oauth2.flow must be loopback or device_code.' }
    if ($flow -eq 'device_code' -and $provider -ne 'microsoft') { throw 'oauth2.flow: device_code supports Microsoft only.' }
    $rawPort = Get-Setting $OAuth 'redirect_port'
    if ($null -ne $rawPort) {
        $port = 0
        if (-not [int]::TryParse([string]$rawPort, [ref]$port) -or $port -lt 1024 -or $port -gt 65535) {
            throw 'oauth2.redirect_port must be a number from 1024 to 65535.'
        }
    }
    if ($provider -eq 'generic') {
        foreach ($key in @('token_url', 'authorize_url')) {
            $urlText = [string](Get-Setting $OAuth $key '')
            if ($key -eq 'token_url' -and [string]::IsNullOrWhiteSpace($urlText)) { throw "Missing 'oauth2.token_url' in YAML." }
            if ([string]::IsNullOrWhiteSpace($urlText)) { continue }
            $url = $null
            if (-not [Uri]::TryCreate($urlText, [UriKind]::Absolute, [ref]$url) -or
                $url.Scheme -ne 'https' -or -not [string]::IsNullOrEmpty($url.UserInfo)) {
                throw "oauth2.$key must be an HTTPS URL without embedded credentials."
            }
        }
    }
    if ($grant -eq 'client_credentials') {
        if ($provider -ne 'microsoft') { throw 'client_credentials currently supports Microsoft only.' }
        if ($tenant -in @('common','organizations','consumers')) {
            throw 'Microsoft client_credentials requires a tenant GUID or tenant domain.'
        }
        [void](Require-String $OAuth 'client_secret' 'oauth2')
    } else {
        [void](Require-String $OAuth 'refresh_token' 'oauth2')
    }
}

function Test-ProfileSettings {
    param([object]$ProfileSettings, [string]$Transport, [hashtable]$Mail)
    if ($ProfileSettings -isnot [System.Collections.IDictionary]) { throw 'Selected profile must be a YAML mapping.' }
    switch ($Transport) {
        'smtp' {
            $smtpHost = Require-String $ProfileSettings 'host' 'smtp'
            if ($smtpHost -match '[\s\x00-\x1F]') { throw 'smtp.host must not contain spaces or control characters.' }
            $rawPort = Get-Setting $ProfileSettings 'port' 587
            $port = 0
            if (-not [int]::TryParse([string]$rawPort, [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
                throw 'smtp.port must be a number from 1 to 65535.'
            }
            $security = [string](Get-Setting $ProfileSettings 'security' 'starttls')
            if ($security -notin @('starttls','ssl','none')) { throw 'smtp.security must be starttls, ssl, or none.' }
            $rawTimeout = Get-Setting $ProfileSettings 'timeout_seconds' 25
            $timeout = 0
            if (-not [int]::TryParse([string]$rawTimeout, [ref]$timeout) -or $timeout -lt 1 -or $timeout -gt 300) {
                throw 'smtp.timeout_seconds must be an integer from 1 to 300.'
            }
            $insecure = Get-Setting $ProfileSettings 'allow_insecure_auth' $false
            if ($insecure -isnot [bool]) { throw 'smtp.allow_insecure_auth must be true or false (unquoted YAML boolean).' }
            $auth = Get-Setting $ProfileSettings 'auth'
            if ($null -ne $auth -and $auth -isnot [System.Collections.IDictionary]) { throw 'smtp.auth must be a YAML mapping.' }
            $mode = [string](Get-Setting $auth 'mode' 'none')
            if ($mode -notin @('none','auto','plain','login','xoauth2')) { throw 'smtp.auth.mode must be none, auto, plain, login, or xoauth2.' }
            if ($security -eq 'none' -and $mode -ne 'none' -and -not $insecure) {
                throw 'Refusing to send SMTP credentials without TLS. To override, set allow_insecure_auth: true.'
            }
            if ($mode -ne 'none') {
                $user = Require-String $auth 'username' 'smtp.auth'
                if ($user -match '[\r\n\x00]') { throw 'SMTP username must not contain NUL or line breaks.' }
                if ($mode -eq 'xoauth2') {
                    Test-OAuthSettings -OAuth (Get-Setting $auth 'oauth2') -Target smtp
                } else {
                    [void](Require-String $auth 'password' 'smtp.auth')
                }
            }
        }
        'cloudflare_api' {
            $id = Require-String $ProfileSettings 'account_id' 'cloudflare_api'
            if ($id -notmatch '^[a-fA-F0-9]{32}$') { throw 'cloudflare_api.account_id must be a 32-character hexadecimal ID.' }
            [void](Require-String $ProfileSettings 'api_token' 'cloudflare_api')
            if (($Mail.to.Count + $Mail.cc.Count + $Mail.bcc.Count) -gt 50) {
                throw 'Cloudflare Email Sending supports at most 50 combined To/Cc/Bcc recipients.'
            }
        }
        'microsoft_graph' {
            Test-OAuthSettings -OAuth (Get-Setting $ProfileSettings 'oauth2') -Target graph
            $oauth = Get-Setting $ProfileSettings 'oauth2'
            if ([string](Get-Setting $oauth 'grant' 'refresh_token') -eq 'client_credentials') {
                $mailbox = [string](Get-Setting $ProfileSettings 'mailbox' $Mail.from)
                [void](Check-MailAddress $mailbox 'microsoft_graph.mailbox')
                if ($mailbox -ne $Mail.from) { throw 'microsoft_graph.mailbox must equal message.from for app authentication.' }
            }
        }
        default { throw "Unsupported profile type: $Transport" }
    }
}

function ConvertTo-HeaderEncoded {
    param([string]$Text)
    # Keep short ASCII subjects plain; encode long subjects to avoid invalid
    # RFC 5322 line lengths without introducing whitespace into long words.
    if ($Text -match '^[\x20-\x7e]*$' -and $Text.Length -le 66) { return $Text }
    # RFC 2047 encoded-words must be <= 75 characters. Keep UTF-8 chunks <= 42 bytes.
    $encoding = [Text.Encoding]::UTF8
    $enumerator = [Globalization.StringInfo]::GetTextElementEnumerator($Text)
    $chunks = New-Object 'System.Collections.Generic.List[string]'
    $chunk = ''
    while ($enumerator.MoveNext()) {
        $element = [string]$enumerator.GetTextElement()
        if ($chunk.Length -gt 0 -and $encoding.GetByteCount($chunk + $element) -gt 42) {
            $chunks.Add(('=?UTF-8?B?' + [Convert]::ToBase64String($encoding.GetBytes($chunk)) + '?='))
            $chunk = ''
        }
        $chunk += $element
    }
    if ($chunk.Length -gt 0) {
        $chunks.Add(('=?UTF-8?B?' + [Convert]::ToBase64String($encoding.GetBytes($chunk)) + '?='))
    }
    return ($chunks -join "`r`n ")
}

function ConvertTo-Base64Lines {
    param([string]$Text)
    $canonical = $Text -replace "`r`n|`r|`n", "`r`n"
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($canonical))
    if ($b64.Length -eq 0) { return '' }
    $lines = New-Object 'System.Collections.Generic.List[string]'
    for ($i = 0; $i -lt $b64.Length; $i += 76) {
        $lines.Add($b64.Substring($i, [Math]::Min(76, $b64.Length - $i)))
    }
    return ($lines -join "`r`n")
}

function Join-MailAddressHeader {
    param([string]$Field, [string[]]$Addresses)
    # Preserve every address while folding a long To/Cc header at comma boundaries.
    $result = "${Field}: "
    $lineLength = $result.Length
    for ($i = 0; $i -lt $Addresses.Count; $i++) {
        $address = $Addresses[$i]
        $segment = if ($i -eq 0) { $address } else { ', ' + $address }
        if ($i -gt 0 -and ($lineLength + $segment.Length) -gt 76) {
            $result += ",`r`n " + $address
            $lineLength = $address.Length + 1
        } else {
            $result += $segment
            $lineLength += $segment.Length
        }
    }
    return $result
}

function New-MimeMessage {
    param([hashtable]$Mail)
    $now = [DateTimeOffset]::Now
    $headerLines = @(
        ('Date: ' + $now.ToString('ddd, dd MMM yyyy HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture) + ' ' + $now.ToString('zzz', [Globalization.CultureInfo]::InvariantCulture).Replace(':','')),
        ('From: ' + $Mail.from),
        (Join-MailAddressHeader -Field 'To' -Addresses $Mail.to),
        ('Subject: ' + (ConvertTo-HeaderEncoded $Mail.subject)),
        'MIME-Version: 1.0',
        'X-Mailer: mailtest.ps1'
    )
    if ($Mail.cc.Count -gt 0) { $headerLines += (Join-MailAddressHeader -Field 'Cc' -Addresses $Mail.cc) }
    # Never add Bcc to MIME headers; it is used for the RCPT TO envelope only.
    if (-not [string]::IsNullOrEmpty($Mail.html) -and -not [string]::IsNullOrEmpty($Mail.text)) {
        $boundary = '=_TestMail_' + [Guid]::NewGuid().ToString('N')
        $headerLines += 'Content-Type: multipart/alternative; boundary="' + $boundary + '"'
        $content = @(
            '--' + $boundary,
            'Content-Type: text/plain; charset=UTF-8',
            'Content-Transfer-Encoding: base64',
            '',
            (ConvertTo-Base64Lines $Mail.text),
            '--' + $boundary,
            'Content-Type: text/html; charset=UTF-8',
            'Content-Transfer-Encoding: base64',
            '',
            (ConvertTo-Base64Lines $Mail.html),
            '--' + $boundary + '--'
        ) -join "`r`n"
    } else {
        $kind = if (-not [string]::IsNullOrEmpty($Mail.html)) { 'text/html' } else { 'text/plain' }
        $headerLines += "Content-Type: $kind; charset=UTF-8"
        $headerLines += 'Content-Transfer-Encoding: base64'
        $value = if ($kind -eq 'text/html') { $Mail.html } else { $Mail.text }
        $content = ConvertTo-Base64Lines $value
    }
    return (($headerLines -join "`r`n") + "`r`n`r`n" + $content + "`r`n")
}

function Get-OAuthHttpError {
    param([object]$Record, [switch]$Detailed)
    $content = ''
    try { $content = [string]$Record.ErrorDetails.Message } catch { }
    if ([string]::IsNullOrWhiteSpace($content)) {
        $response = $null
        try { $response = $Record.Exception.Response } catch { }
        if ($null -ne $response) {
            # PowerShell 7: HttpResponseMessage; Windows PowerShell 5.1: HttpWebResponse.
            try { $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() } catch { }
            if ([string]::IsNullOrWhiteSpace($content)) {
                try {
                    $stream = $response.GetResponseStream()
                    if ($null -ne $stream) {
                        $reader = [IO.StreamReader]::new($stream)
                        try { $content = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    }
                } catch { }
            }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($content)) {
        try {
            $parsed = ConvertFrom-Json -InputObject $content
            $err = [string](Get-Setting $parsed 'error')
            if (-not [string]::IsNullOrWhiteSpace($err)) {
                $description = ([string](Get-Setting $parsed 'error_description' '') -replace '\s+', ' ').Trim()
                if ($Detailed -and $description.Length -gt 0) {
                    if ($description.Length -gt 300) { $description = $description.Substring(0, 300) + '...' }
                    return "$err ($description)"
                }
                return $err
            }
        } catch { }
    }
    return 'http_error'
}

function Invoke-MicrosoftDeviceCode {
    param([string]$Tenant, [string]$ClientId, [string]$Scope)
    $root = "https://login.microsoftonline.com/$Tenant/oauth2/v2.0"
    $challenge = Invoke-RestMethod -Method Post -Uri "$root/devicecode" -Body @{ client_id = $ClientId; scope = $Scope } -ErrorAction Stop
    Write-Host 'Microsoft sign-in required (one-time bootstrap):'
    Write-Host ([string]$challenge.message)
    $interval = [Math]::Max(5, [int]$challenge.interval)
    $deadline = [DateTime]::UtcNow.AddSeconds([int]$challenge.expires_in)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds $interval
        try {
            return Invoke-RestMethod -Method Post -Uri "$root/token" -Body @{
                grant_type = 'urn:ietf:params:oauth:grant-type:device_code'
                client_id = $ClientId
                device_code = [string]$challenge.device_code
            } -ErrorAction Stop
        } catch {
            $errorName = Get-OAuthHttpError $_
            switch ($errorName) {
                'authorization_pending' { continue }
                'slow_down' { $interval += 5; continue }
                'authorization_declined' { throw 'Device-code sign-in was declined.' }
                'expired_token' { throw 'Device-code sign-in expired. Run -InitOAuth again.' }
                default { throw "Device-code authorization failed: $errorName" }
            }
        }
    }
    throw 'Device-code sign-in timed out.'
}

function ConvertTo-Base64Url {
    param([byte[]]$Bytes)
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-PkceChallenge {
    param([string]$Verifier)
    return ConvertTo-Base64Url ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($Verifier)))
}

function Get-AuthorizationCode {
    param([string]$Pasted, [string]$ExpectedState)
    $text = $Pasted.Trim()
    if ($text -notmatch '^https?://') {
        if ([string]::IsNullOrWhiteSpace($text)) { throw 'No authorization code was provided.' }
        return $text
    }
    $query = ([Uri]$text).Query.TrimStart('?')
    $pairs = @{}
    foreach ($part in ($query -split '&')) {
        if ($part.Length -eq 0) { continue }
        $kv = $part -split '=', 2
        $value = if ($kv.Count -gt 1) { [Uri]::UnescapeDataString($kv[1].Replace('+', ' ')) } else { '' }
        $pairs[[Uri]::UnescapeDataString($kv[0])] = $value
    }
    if ($pairs.ContainsKey('error')) { throw "Authorization failed: $($pairs['error'])" }
    if ($pairs['state'] -ne $ExpectedState) { throw 'The state value in the pasted URL does not match this sign-in attempt.' }
    if ([string]::IsNullOrWhiteSpace([string]$pairs['code'])) { throw 'The pasted URL has no code parameter.' }
    return [string]$pairs['code']
}

function Get-OAuthSettings {
    # Endpoints, scope and flow for one oauth2 mapping.
    param([object]$OAuth, [ValidateSet('smtp','graph')][string]$Target)
    $provider = [string](Get-Setting $OAuth 'provider' 'microsoft')
    $grant = [string](Get-Setting $OAuth 'grant' 'refresh_token')
    $tenant = [string](Get-Setting $OAuth 'tenant' 'organizations')
    $settings = @{
        Provider = $provider; Grant = $grant; Tenant = $tenant
        ClientId = Require-String $OAuth 'client_id' 'oauth2'
        ClientSecret = [string](Get-Setting $OAuth 'client_secret' '')
        Scope = [string](Get-Setting $OAuth 'scope' '')
        Flow = [string](Get-Setting $OAuth 'flow' 'loopback')
        Port = [int](Get-Setting $OAuth 'redirect_port' 0)
        AuthorizeUrl = ''; TokenUrl = ''; InitScope = ''
        ExtraParameters = New-Object 'System.Collections.Generic.List[string]'
    }
    if ($provider -eq 'microsoft') {
        if ($tenant -notmatch '^[A-Za-z0-9.\-]+$') { throw 'Invalid oauth2.tenant.' }
        $settings.AuthorizeUrl = "https://login.microsoftonline.com/$tenant/oauth2/v2.0/authorize"
        $settings.TokenUrl = "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token"
        $settings.ExtraParameters.Add('response_mode=query')
        if ([string]::IsNullOrEmpty($settings.Scope)) {
            if ($grant -eq 'client_credentials') {
                $settings.Scope = if ($Target -eq 'smtp') { 'https://outlook.office365.com/.default' } else { 'https://graph.microsoft.com/.default' }
            } else {
                $settings.Scope = if ($Target -eq 'smtp') { 'https://outlook.office.com/SMTP.Send offline_access' } else { 'https://graph.microsoft.com/Mail.Send offline_access' }
            }
        }
        $settings.InitScope = $settings.Scope
    } elseif ($provider -eq 'google') {
        $settings.AuthorizeUrl = 'https://accounts.google.com/o/oauth2/v2/auth'
        $settings.TokenUrl = 'https://oauth2.googleapis.com/token'
        $settings.ExtraParameters.Add('access_type=offline')
        $settings.ExtraParameters.Add('prompt=consent')
        $settings.InitScope = if ([string]::IsNullOrEmpty($settings.Scope)) { 'https://mail.google.com/' } else { $settings.Scope }
    } elseif ($provider -eq 'generic') {
        $settings.AuthorizeUrl = [string](Get-Setting $OAuth 'authorize_url' '')
        $settings.TokenUrl = Require-String $OAuth 'token_url' 'oauth2'
        $settings.InitScope = $settings.Scope
    } else {
        throw "Unsupported oauth2.provider: $provider"
    }
    if ($settings.TokenUrl -notmatch '^https://') { throw 'OAuth2 token_url must use HTTPS.' }
    return $settings
}

function Get-FreePort {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $probe.Start(); return ([Net.IPEndPoint]$probe.LocalEndpoint).Port }
    finally { $probe.Stop() }
}

function Wait-LoopbackRedirect {
    # Local service: answers the browser redirect and returns the full redirected URL.
    param([int]$Port, [int]$TimeoutSeconds = 300)
    # HttpListener does not always fail on a busy port, so probe it first.
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    $probe.Start()
    $probe.Stop()
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add("http://localhost:$Port/")
    $listener.Start()
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ($true) {
            $pending = $listener.GetContextAsync()
            while (-not $pending.Wait(500)) {
                if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for the browser redirect.' }
            }
            $context = $pending.Result
            $rawUrl = [string]$context.Request.RawUrl
            $done = $rawUrl -match '[?&](code|error)='
            $page = if ($done) { 'Authorization received. You can close this tab and return to the terminal.' } else { 'Waiting for authorization.' }
            $bytes = [Text.Encoding]::UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>mailtest</title><p>$page</p>")
            $context.Response.ContentType = 'text/html; charset=utf-8'
            $context.Response.ContentLength64 = $bytes.Length
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            $context.Response.Close()
            if ($done) { return "http://localhost:$Port$rawUrl" }
        }
    } finally {
        $listener.Close()
    }
}

function Invoke-AuthorizationCodeFlow {
    # Authorization code + PKCE (S256) with a loopback redirect. Falls back to pasting the redirected
    # URL when the local listener cannot start.
    param([hashtable]$Settings, [string]$ClientSecret)
    if ([string]::IsNullOrWhiteSpace($Settings.AuthorizeUrl)) { throw 'oauth2.authorize_url is required for -InitOAuth with provider: generic.' }
    $port = if ($Settings.Port -gt 0) { $Settings.Port } else { Get-FreePort }
    $redirectUri = "http://localhost:$port"
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $stateBytes = New-Object byte[] 16
    $verifierBytes = New-Object byte[] 32
    $random.GetBytes($stateBytes)
    $random.GetBytes($verifierBytes)
    $state = ConvertTo-Base64Url $stateBytes
    $verifier = ConvertTo-Base64Url $verifierBytes
    $parts = @(
        ('client_id=' + [Uri]::EscapeDataString($Settings.ClientId)),
        ('redirect_uri=' + [Uri]::EscapeDataString($redirectUri)),
        'response_type=code',
        ('scope=' + [Uri]::EscapeDataString($Settings.InitScope)),
        ('state=' + $state),
        ('code_challenge=' + (Get-PkceChallenge $verifier)),
        'code_challenge_method=S256'
    ) + @($Settings.ExtraParameters)
    $authorizeUrl = $Settings.AuthorizeUrl + '?' + ($parts -join '&')
    Write-Host 'Open this URL in a browser, sign in and approve access:'
    Write-Host ''
    Write-Host $authorizeUrl
    Write-Host ''
    $redirected = $null
    try {
        Write-Host "Waiting for the browser redirect on $redirectUri ..."
        $redirected = Wait-LoopbackRedirect -Port $port
    } catch [System.Net.HttpListenerException], [System.Net.Sockets.SocketException] {
        Write-Host "Cannot listen on $redirectUri ($($_.Exception.Message))."
        Write-Host "After approving, the browser shows a connection error for $redirectUri. That is expected:"
        Write-Host 'copy the full address-bar URL (or just its code= value) and paste it here.'
        $redirected = Read-Host 'Redirected URL or code'
    }
    $code = Get-AuthorizationCode -Pasted $redirected -ExpectedState $state
    $form = @{
        grant_type = 'authorization_code'; code = $code; client_id = $Settings.ClientId
        redirect_uri = $redirectUri; code_verifier = $verifier
    }
    if (-not [string]::IsNullOrWhiteSpace($ClientSecret)) { $form.client_secret = $ClientSecret }
    try { return Invoke-RestMethod -Method Post -Uri $Settings.TokenUrl -Body $form -ErrorAction Stop }
    catch { throw "Token exchange failed: $(Get-OAuthHttpError $_ -Detailed)" }
}

function Write-RefreshTokenNotice {
    param([string]$RefreshToken, [string]$EnvName)
    Write-Host ''
    Write-Host '[OK] Authorization complete.'
    Write-Warning 'The refresh token below is a long-lived secret. Store it in a secret manager or environment variable; never commit it or share logs.'
    Write-Host "REFRESH_TOKEN=$RefreshToken"
    Write-Host ''
    Write-Host 'Next steps:'
    Write-Host "  1. Keep the token, for example for this session:  `$env:$EnvName = '<token>'"
    Write-Host "  2. Test it with this tool:                         .\mailtest.ps1 -Profile $ProfileName"
    Write-Host '  3. Give it to LMail: OAuth2Token.FromRefreshToken(token) - see docs/MAILTEST.md'
}

function Test-Interactive {
    return (-not [Console]::IsInputRedirected)
}

function Read-Choice {
    param([string]$Prompt, [int]$Count)
    $answer = (Read-Host $Prompt).Trim()
    if ($answer -ieq 'q') { exit 0 }
    $number = 0
    if (-not [int]::TryParse($answer, [ref]$number) -or $number -lt 1 -or $number -gt $Count) { throw "Choose a number from 1 to $Count, or q to quit." }
    return $number
}

function Get-OAuthProviders {
    # Profiles that sign in with OAuth2 and keep a refresh token: the providers --init-oauth can authorize.
    param([object]$Profiles)
    $found = New-Object 'System.Collections.Generic.List[object]'
    foreach ($name in @($Profiles.Keys)) {
        $profile = $Profiles[$name]
        if ($profile -isnot [System.Collections.IDictionary]) { continue }
        $type = [string](Get-Setting $profile 'type' '')
        $oauth = $null
        $where = ''
        if ($type -eq 'smtp') {
            $auth = Get-Setting $profile 'auth'
            if ([string](Get-Setting $auth 'mode' 'none') -eq 'xoauth2') {
                $oauth = Get-Setting $auth 'oauth2'
                $where = 'SMTP ' + [string](Get-Setting $profile 'host' '')
            }
        } elseif ($type -eq 'microsoft_graph') {
            $oauth = Get-Setting $profile 'oauth2'
            $where = 'Graph sendMail'
        }
        if ($oauth -isnot [System.Collections.IDictionary]) { continue }
        if ([string](Get-Setting $oauth 'grant' 'refresh_token') -ne 'refresh_token') { continue }
        $provider = switch ([string](Get-Setting $oauth 'provider' 'microsoft')) { 'microsoft' { 'Microsoft' } 'google' { 'Google' } default { 'OAuth2 provider' } }
        $found.Add(@{ Name = [string]$name; Label = "$provider - $where" })
    }
    return ,$found
}

function Select-OAuthProvider {
    param([object]$Profiles)
    $providers = Get-OAuthProviders -Profiles $Profiles
    if ($providers.Count -eq 0) { throw 'No OAuth2 profile (smtp.auth.mode: xoauth2 or microsoft_graph with grant: refresh_token) in the YAML.' }
    if (-not (Test-Interactive)) {
        throw ('Choose a profile with -Profile: ' + (($providers | ForEach-Object { $_.Name }) -join ', '))
    }
    Write-Host 'Authorize which provider?'
    for ($i = 0; $i -lt $providers.Count; $i++) {
        Write-Host ('  {0}) {1,-22} {2}' -f ($i + 1), $providers[$i].Name, $providers[$i].Label)
    }
    Write-Host '  q) Quit'
    return $providers[(Read-Choice 'Select' $providers.Count) - 1].Name
}

function Invoke-OAuthInit {
    # Interactive sign-in: prints the refresh token. Nothing is stored and no mail is sent.
    param([object]$OAuth, [ValidateSet('smtp','graph')][string]$Target)
    $settings = Get-OAuthSettings -OAuth $OAuth -Target $Target
    if ($settings.ClientId -in @('00000000-0000-0000-0000-000000000000', 'example.apps.googleusercontent.com', 'your-client-id')) {
        throw "oauth2.client_id of profile '$ProfileName' is still the template placeholder. Edit $absoluteConfig first."
    }
    if ($settings.Provider -eq 'microsoft' -and $settings.Flow -eq 'device_code') {
        $result = Invoke-MicrosoftDeviceCode -Tenant $settings.Tenant -ClientId $settings.ClientId -Scope $settings.InitScope
    } else {
        $secret = ''
        if ($settings.Provider -eq 'google' -or -not [string]::IsNullOrWhiteSpace($settings.ClientSecret)) {
            $secret = Resolve-Secret $settings.ClientSecret 'oauth2.client_secret'
        }
        $result = Invoke-AuthorizationCodeFlow -Settings $settings -ClientSecret $secret
    }
    if ([string]::IsNullOrWhiteSpace([string]$result.refresh_token)) { throw 'No refresh token returned; ensure offline access was granted.' }
    $envName = 'MAILTEST_REFRESH_TOKEN'
    if ([string](Get-Setting $OAuth 'refresh_token' '') -match '^env:([A-Za-z_][A-Za-z0-9_]*)$') { $envName = $Matches[1] }
    Write-RefreshTokenNotice -RefreshToken ([string]$result.refresh_token) -EnvName $envName
}

function Get-OAuthAccessToken {
    param([object]$OAuth, [ValidateSet('smtp','graph')][string]$Target)
    $grant = [string](Get-Setting $OAuth 'grant' 'refresh_token')
    if ($grant -eq 'access_token') {
        return Resolve-Secret (Get-Setting $OAuth 'access_token') 'oauth2.access_token'
    }
    $settings = Get-OAuthSettings -OAuth $OAuth -Target $Target
    if ($grant -eq 'client_credentials') {
        if ($settings.Provider -ne 'microsoft') { throw 'client_credentials is currently supported only for Microsoft.' }
        if ($settings.Tenant -in @('common','organizations','consumers')) { throw 'Microsoft client_credentials requires a specific tenant ID or tenant domain, not common/organizations/consumers.' }
        $form = @{
            grant_type = 'client_credentials'; client_id = $settings.ClientId
            client_secret = Resolve-Secret $settings.ClientSecret 'oauth2.client_secret'; scope = $settings.Scope
        }
        $result = Invoke-RestMethod -Method Post -Uri $settings.TokenUrl -Body $form -ErrorAction Stop
        $token = [string](Get-Setting $result 'access_token' '')
        if ([string]::IsNullOrWhiteSpace($token)) { throw 'OAuth client_credentials response had no access_token.' }
        return $token
    }
    if ($grant -ne 'refresh_token') { throw "Unsupported oauth2.grant: $grant" }
    $refreshToken = Resolve-Secret (Get-Setting $OAuth 'refresh_token') 'oauth2.refresh_token'
    $form = @{ grant_type = 'refresh_token'; client_id = $settings.ClientId; refresh_token = $refreshToken }
    if (-not [string]::IsNullOrWhiteSpace($settings.Scope)) { $form.scope = $settings.Scope }
    if (-not [string]::IsNullOrWhiteSpace($settings.ClientSecret)) { $form.client_secret = Resolve-Secret $settings.ClientSecret 'oauth2.client_secret' }
    try { $result = Invoke-RestMethod -Method Post -Uri $settings.TokenUrl -Body $form -ErrorAction Stop }
    catch {
        $errorName = Get-OAuthHttpError $_ -Detailed
        throw "OAuth refresh failed: $errorName. Run -InitOAuth to sign in again."
    }
    $newRefresh = [string](Get-Setting $result 'refresh_token' '')
    if (-not [string]::IsNullOrWhiteSpace($newRefresh) -and $newRefresh -ne $refreshToken) {
        Write-Warning 'The provider rotated the refresh token. Update your stored value before the next run.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$result.access_token)) { throw 'OAuth token endpoint returned no access_token.' }
    return [string]$result.access_token
}

function Write-SmtpTrace {
    param([string]$Message)
    if ($script:TraceSmtp) { Write-Host "[SMTP] $Message" }
}

function New-SmtpStreams {
    param([IO.Stream]$Stream)
    $reader = [IO.StreamReader]::new($Stream, [Text.Encoding]::ASCII, $false, 1024, $true)
    $writer = [IO.StreamWriter]::new($Stream, [Text.UTF8Encoding]::new($false), 1024, $true)
    $writer.NewLine = "`r`n"
    $writer.AutoFlush = $true
    return @{ Reader = $reader; Writer = $writer }
}

function Read-SmtpResponse {
    param([IO.StreamReader]$Reader, [switch]$Sensitive)
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $expectedCode = -1
    for ($i = 0; $i -lt 100; $i++) {
        $line = $Reader.ReadLine()
        if ($null -eq $line) { throw 'SMTP server closed the connection.' }
        if ($line -notmatch '^(\d{3})([ -])(.*)$') { throw 'Malformed SMTP server response.' }
        $code = [int]$Matches[1]
        $separator = $Matches[2]
        if ($expectedCode -ge 0 -and $expectedCode -ne $code) { throw 'Inconsistent SMTP multiline reply code.' }
        $expectedCode = $code
        $lines.Add($line)
        if ($Sensitive) { Write-SmtpTrace "S: $code (authentication response redacted)" }
        else { Write-SmtpTrace "S: $line" }
        if ($separator -eq ' ') { return @{ Code = $code; Lines = $lines.ToArray(); Text = ($lines -join ' | ') } }
    }
    throw 'SMTP server sent more than 100 reply lines.'
}

function Invoke-SmtpCommand {
    param([IO.StreamReader]$Reader, [IO.StreamWriter]$Writer, [string]$Command, [switch]$Sensitive)
    if ($Sensitive) { Write-SmtpTrace 'C: [AUTH data hidden]' }
    else { Write-SmtpTrace "C: $Command" }
    $Writer.WriteLine($Command)
    $Writer.Flush()
    return Read-SmtpResponse -Reader $Reader -Sensitive:$Sensitive
}

function Assert-SmtpReply {
    param([object]$Reply, [int[]]$Codes, [string]$Stage, [switch]$Sensitive)
    if ($Reply.Code -notin $Codes) {
        if ($Sensitive) { throw "SMTP $Stage rejected (code $($Reply.Code)); credentials/token not logged." }
        throw "SMTP $Stage failed: $($Reply.Text)"
    }
}

function Get-SmtpCapabilities {
    param([object]$Reply)
    $extensions = @()
    foreach ($line in $Reply.Lines) {
        if ($line.Length -ge 4) { $extensions += $line.Substring(4) }
    }
    return ,$extensions
}

function Invoke-SmtpAuth {
    param([IO.StreamReader]$Reader, [IO.StreamWriter]$Writer, [object]$Auth, [string[]]$Capabilities, [string]$Mode, [string]$AccessToken = '')
    $authLines = @($Capabilities | Where-Object { $_ -match '^AUTH(?:\s|=)' })
    $supported = @()
    foreach ($line in $authLines) {
        $supported += @((($line -replace '^AUTH(?:\s+|=)', '').Trim()) -split '\s+')
    }
    $supported = @($supported | ForEach-Object { $_.ToUpperInvariant() })
    if ($Mode -eq 'auto') {
        if ('PLAIN' -in $supported) { $Mode = 'plain' }
        elseif ('LOGIN' -in $supported) { $Mode = 'login' }
        else { throw 'Server did not advertise supported AUTH mechanisms (PLAIN/LOGIN).' }
    }
    if ($Mode.ToUpperInvariant() -notin $supported) { throw "SMTP server did not advertise AUTH $($Mode.ToUpperInvariant())." }
    $username = Require-String $Auth 'username' 'smtp.auth'
    if ($username -match '[\r\n\x00]') { throw 'Invalid SMTP username.' }
    if ($Mode -eq 'xoauth2') {
        if ([string]::IsNullOrWhiteSpace($AccessToken)) { throw 'OAuth2 access token was not provided.' }
        $payload = "user=$username" + [char]1 + "auth=Bearer $AccessToken" + [char]1 + [char]1
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
        $reply = Invoke-SmtpCommand $Reader $Writer "AUTH XOAUTH2 $encoded" -Sensitive
        if ($reply.Code -eq 334) {
            # Per XOAUTH2 convention, an empty reply terminates the failed challenge.
            $reply = Invoke-SmtpCommand $Reader $Writer '' -Sensitive
        }
        Assert-SmtpReply $reply @(235) 'XOAUTH2 authentication' -Sensitive
        return
    }
    $password = Resolve-Secret (Get-Setting $Auth 'password') 'smtp.auth.password'
    if ($Mode -eq 'plain') {
        $raw = [string]([char]0) + $username + [char]0 + $password
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($raw))
        $reply = Invoke-SmtpCommand $Reader $Writer "AUTH PLAIN $encoded" -Sensitive
        if ($reply.Code -eq 334) { $reply = Invoke-SmtpCommand $Reader $Writer $encoded -Sensitive }
        Assert-SmtpReply $reply @(235) 'PLAIN authentication' -Sensitive
        return
    }
    if ($Mode -eq 'login') {
        $reply = Invoke-SmtpCommand $Reader $Writer 'AUTH LOGIN' -Sensitive
        Assert-SmtpReply $reply @(334) 'LOGIN username challenge' -Sensitive
        $reply = Invoke-SmtpCommand $Reader $Writer ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($username))) -Sensitive
        Assert-SmtpReply $reply @(334) 'LOGIN password challenge' -Sensitive
        $reply = Invoke-SmtpCommand $Reader $Writer ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($password))) -Sensitive
        Assert-SmtpReply $reply @(235) 'LOGIN authentication' -Sensitive
        return
    }
    throw "Unsupported SMTP auth mode: $Mode"
}

function Send-SmtpMail {
    param([object]$ProfileSettings, [hashtable]$Mail)
    $hostName = Require-String $ProfileSettings 'host' 'smtp'
    $port = [int](Get-Setting $ProfileSettings 'port' 587)
    if ($port -lt 1 -or $port -gt 65535) { throw 'smtp.port must be 1-65535.' }
    $security = [string](Get-Setting $ProfileSettings 'security' 'starttls')
    if ($security -notin @('starttls','ssl','none')) { throw 'smtp.security must be starttls, ssl, or none.' }
    $auth = Get-Setting $ProfileSettings 'auth'
    $mode = [string](Get-Setting $auth 'mode' 'none')
    if ($mode -notin @('none','auto','plain','login','xoauth2')) { throw 'smtp.auth.mode must be none, auto, plain, login, or xoauth2.' }
    $allowInsecure = [bool](Get-Setting $ProfileSettings 'allow_insecure_auth' $false)
    if ($security -eq 'none' -and $mode -ne 'none' -and -not $allowInsecure) {
        throw 'Refusing to send SMTP credentials over plaintext. Use TLS or explicitly set allow_insecure_auth: true (not recommended).'
    }
    if ($security -eq 'none') { Write-Warning 'SMTP connection is not encrypted. This is appropriate only for a trusted relay/test environment.' }
    $timeout = [int](Get-Setting $ProfileSettings 'timeout_seconds' 25)
    if ($timeout -lt 1 -or $timeout -gt 300) { throw 'smtp.timeout_seconds must be 1-300.' }
    $timeoutMs = $timeout * 1000
    # Obtain OAuth tokens BEFORE opening SMTP socket: device login may take minutes.
    $oauthAccessToken = ''
    if ($mode -eq 'xoauth2') {
        $oauthConfig = Get-Setting $auth 'oauth2'
        if ($null -eq $oauthConfig) { throw 'smtp.auth.oauth2 is required for xoauth2.' }
        $oauthAccessToken = Get-OAuthAccessToken -OAuth $oauthConfig -Target 'smtp'
    }
    $tcp = [Net.Sockets.TcpClient]::new()
    $sslStream = $null
    $reader = $null
    $writer = $null
    try {
        $connectTask = $tcp.ConnectAsync($hostName, $port)
        try { $connected = $connectTask.Wait($timeoutMs) }
        catch { throw "TCP connection failed: ${hostName}:${port}: $($_.Exception.GetBaseException().Message)" }
        if (-not $connected) { throw "TCP connection timeout: ${hostName}:${port}" }
        $network = $tcp.GetStream()
        $network.ReadTimeout = $timeoutMs
        $network.WriteTimeout = $timeoutMs
        if ($security -eq 'ssl') {
            $sslStream = [Net.Security.SslStream]::new($network, $false)
            $sslProtocols = if ($PSVersionTable.PSVersion.Major -lt 6) {
                [System.Security.Authentication.SslProtocols]::Tls12
            } else {
                [System.Security.Authentication.SslProtocols]::None
            }
            $sslStream.AuthenticateAsClient($hostName, $null, $sslProtocols, $false)
            $sslStream.ReadTimeout = $timeoutMs
            $sslStream.WriteTimeout = $timeoutMs
            $streams = New-SmtpStreams $sslStream
        } else {
            $streams = New-SmtpStreams $network
        }
        $reader = $streams.Reader
        $writer = $streams.Writer
        $reply = Read-SmtpResponse $reader
        Assert-SmtpReply $reply @(220) 'greeting'
        $ehloName = 'localhost'
        $reply = Invoke-SmtpCommand $reader $writer "EHLO $ehloName"
        Assert-SmtpReply $reply @(250) 'EHLO'
        $capabilities = Get-SmtpCapabilities $reply
        if ($security -eq 'starttls') {
            if (-not @($capabilities | Where-Object { $_ -match '^STARTTLS(?:\s|$)' }).Count) {
                throw 'SMTP server does not advertise STARTTLS; refusing plaintext fallback.'
            }
            $reply = Invoke-SmtpCommand $reader $writer 'STARTTLS'
            Assert-SmtpReply $reply @(220) 'STARTTLS'
            $reader.Dispose()
            $writer.Dispose()
            $reader = $null
            $writer = $null
            $sslStream = [Net.Security.SslStream]::new($network, $false)
            $sslProtocols = if ($PSVersionTable.PSVersion.Major -lt 6) {
                [System.Security.Authentication.SslProtocols]::Tls12
            } else {
                [System.Security.Authentication.SslProtocols]::None
            }
            $sslStream.AuthenticateAsClient($hostName, $null, $sslProtocols, $false)
            $sslStream.ReadTimeout = $timeoutMs
            $sslStream.WriteTimeout = $timeoutMs
            $streams = New-SmtpStreams $sslStream
            $reader = $streams.Reader
            $writer = $streams.Writer
            $reply = Invoke-SmtpCommand $reader $writer "EHLO $ehloName"
            Assert-SmtpReply $reply @(250) 'EHLO after STARTTLS'
            $capabilities = Get-SmtpCapabilities $reply
        }
        if ($mode -ne 'none') { Invoke-SmtpAuth -Reader $reader -Writer $writer -Auth $auth -Capabilities $capabilities -Mode $mode -AccessToken $oauthAccessToken }
        $reply = Invoke-SmtpCommand $reader $writer ('MAIL FROM:<' + $Mail.from + '>')
        Assert-SmtpReply $reply @(250) 'MAIL FROM'
        $recipients = @(@($Mail.to) + @($Mail.cc) + @($Mail.bcc) | Select-Object -Unique)
        foreach ($recipient in $recipients) {
            $reply = Invoke-SmtpCommand $reader $writer ("RCPT TO:<$recipient>")
            Assert-SmtpReply $reply @(250,251,252) "RCPT TO $recipient"
        }
        $reply = Invoke-SmtpCommand $reader $writer 'DATA'
        Assert-SmtpReply $reply @(354) 'DATA'
        $mime = New-MimeMessage $Mail
        # DATA must use CRLF and dot stuffing; no SMTPUTF8 envelope is used.
        $normalized = $mime -replace "`r`n", "`n" -replace "`r", "`n"
        $dataLines = $normalized.TrimEnd([char]10).Split([char]10)
        foreach ($line in $dataLines) {
            if ($line.StartsWith('.')) { $writer.WriteLine('.' + $line) }
            else { $writer.WriteLine($line) }
        }
        $writer.WriteLine('.')
        $writer.Flush()
        $reply = Read-SmtpResponse $reader
        Assert-SmtpReply $reply @(250) 'message submission'
        Write-Host "[OK] SMTP accepted the test mail: ${hostName}:${port} ($security); recipients=$($recipients.Count)"
        Write-Host "     Server reply: $($reply.Text)"
    } finally {
        if ($null -ne $writer) {
            try { [void](Invoke-SmtpCommand $reader $writer 'QUIT') } catch { }
            try { $writer.Dispose() } catch { }
        }
        if ($null -ne $reader) { try { $reader.Dispose() } catch { } }
        if ($null -ne $sslStream) { try { $sslStream.Dispose() } catch { } }
        $tcp.Dispose()
    }
}

function Send-CloudflareMail {
    param([object]$ProfileSettings, [hashtable]$Mail)
    $accountId = Require-String $ProfileSettings 'account_id' 'cloudflare_api'
    if ($accountId -notmatch '^[a-fA-F0-9]{32}$') { throw 'cloudflare_api.account_id must be a 32-character hex ID.' }
    $token = Resolve-Secret (Get-Setting $ProfileSettings 'api_token') 'cloudflare_api.api_token'
    $payload = @{ from = $Mail.from; to = @($Mail.to); subject = $Mail.subject }
    if ($Mail.cc.Count -gt 0) { $payload.cc = @($Mail.cc) }
    if ($Mail.bcc.Count -gt 0) { $payload.bcc = @($Mail.bcc) }
    if (-not [string]::IsNullOrEmpty($Mail.text)) { $payload.text = $Mail.text }
    if (-not [string]::IsNullOrEmpty($Mail.html)) { $payload.html = $Mail.html }
    $uri = "https://api.cloudflare.com/client/v4/accounts/$accountId/email/sending/send"
    $json = ConvertTo-Json -InputObject $payload -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $response = Invoke-RestMethod -Method Post -Uri $uri -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json; charset=utf-8' -Body $bytes -ErrorAction Stop
    if (-not [bool](Get-Setting $response 'success' $false)) {
        $errors = @((Get-Setting $response 'errors' @()) | ForEach-Object { [string](Get-Setting $_ 'message') })
        throw ('Cloudflare API returned success=false: ' + ($errors -join '; '))
    }
    $result = Get-Setting $response 'result'
    if ($null -eq $result) { throw 'Cloudflare API returned success=true without a result object.' }
    $delivered = @((Get-Setting $result 'delivered' @()) | Where-Object { $null -ne $_ })
    $queued = @((Get-Setting $result 'queued' @()) | Where-Object { $null -ne $_ })
    $suppressed = @((Get-Setting $result 'suppressed_recipients' @()) | Where-Object { $null -ne $_ })
    $bounced = @((Get-Setting $result 'permanent_bounces' @()) | Where-Object { $null -ne $_ })
    $messageId = [string](Get-Setting $result 'message_id' '(not returned)')
    Write-Host "[OK] Cloudflare API accepted the request. message_id=$messageId; delivered=$($delivered.Count); queued=$($queued.Count)"
    if ($suppressed.Count -gt 0 -or $bounced.Count -gt 0) {
        throw "Cloudflare reports suppressed=$($suppressed.Count), permanent_bounces=$($bounced.Count); not a clean delivery test."
    }
}

function Send-GraphMail {
    param([object]$ProfileSettings, [hashtable]$Mail)
    $oauth = Get-Setting $ProfileSettings 'oauth2'
    if ($null -eq $oauth) { throw 'microsoft_graph.oauth2 must be specified.' }
    if ([string](Get-Setting $oauth 'provider' 'microsoft') -ne 'microsoft') { throw 'microsoft_graph requires oauth2.provider: microsoft.' }
    $token = Get-OAuthAccessToken -OAuth $oauth -Target graph
    $grant = [string](Get-Setting $oauth 'grant' 'refresh_token')
    if ($grant -eq 'client_credentials') {
        $mailbox = [string](Get-Setting $ProfileSettings 'mailbox' $Mail.from)
        [void](Check-MailAddress $mailbox 'microsoft_graph.mailbox')
        if ($mailbox -ne $Mail.from) { throw 'For Graph application auth, microsoft_graph.mailbox must equal message.from.' }
        $endpoint = 'https://graph.microsoft.com/v1.0/users/' + [Uri]::EscapeDataString($mailbox) + '/sendMail'
    } else {
        $endpoint = 'https://graph.microsoft.com/v1.0/me/sendMail'
        Write-Warning 'Graph delegated mode sends as the signed-in user (not necessarily message.from). Configure SendAs separately if needed.'
    }
    $toRecipients = @($Mail.to | ForEach-Object { @{ emailAddress = @{ address = $_ } } })
    $message = @{ subject = $Mail.subject; body = @{ contentType = 'Text'; content = $Mail.text }; toRecipients = $toRecipients }
    if (-not [string]::IsNullOrEmpty($Mail.html)) { $message.body = @{ contentType = 'HTML'; content = $Mail.html } }
    if ($Mail.cc.Count -gt 0) { $message.ccRecipients = @($Mail.cc | ForEach-Object { @{ emailAddress = @{ address = $_ } } }) }
    if ($Mail.bcc.Count -gt 0) { $message.bccRecipients = @($Mail.bcc | ForEach-Object { @{ emailAddress = @{ address = $_ } } }) }
    $request = @{ message = $message; saveToSentItems = $false }
    $json = ConvertTo-Json -InputObject $request -Depth 12 -Compress
    [void](Invoke-RestMethod -Method Post -Uri $endpoint -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -ErrorAction Stop)
    Write-Host '[OK] Microsoft Graph accepted sendMail (HTTP 202). This is not proof of final inbox delivery.'
}

# Embedded first-run template; never loaded from another file.
$script:EmbeddedMailConfig = @'
# Mail Send Test configuration, shared by mailtest.ps1 and mailtest.sh
# -----------------------------------------------------------------------------
# The first run of a script creates this file beside the script and stops (no mail is sent).
# Edit message.* and the profile you want, save, and run again. See docs/MAILTEST.md.
#
#   ./mailtest.sh --validate-only                 .\mailtest.ps1 -ValidateOnly
#   ./mailtest.sh --profile smtp_ssl              .\mailtest.ps1 -Profile smtp_ssl
#   ./mailtest.sh --profile gmail_oauth --init-oauth
#                                                 .\mailtest.ps1 -Profile gmail_oauth -InitOAuth
#
# --init-oauth / -InitOAuth signs a user in through a local web server on this machine, prints
# the refresh token (give it to LMail, or export it as the env: variable the profile names) and
# exits. It does not send mail.
#
# Secrets: write env:NAME instead of the value and export NAME in your shell. Never commit real
# passwords, client secrets, API tokens or refresh tokens.
# All host names, ids and addresses below are placeholders; replace them.
# Use spaces for indentation (no tabs). Only simple YAML is supported: maps, lists, comments, | text.
# -----------------------------------------------------------------------------
default_profile: smtp_starttls

message:
  from: "sender@example.com"
  to:
    - "recipient@example.net"
  subject: "LMail mailtest: connectivity check"
  text: |
    This is a connectivity test message.
    Being accepted does not prove inbox delivery; check the recipient and spam folders.
  # html: "<p>This is a test.</p>"
  # cc: ["other@example.net"]
  # bcc: ["hidden@example.net"]

profiles:
  # Plain SMTP, port 587 + STARTTLS. Works for most mailboxes with an app password.
  smtp_starttls:
    type: smtp
    host: "smtp.example.com"
    port: 587
    security: starttls             # starttls | ssl | none
    timeout_seconds: 25
    auth:
      mode: auto                  # auto | login | plain | none | xoauth2
      username: "sender@example.com"
      password: "env:SMTP_PASSWORD"

  # Plain SMTP, port 465 + implicit TLS.
  smtp_ssl:
    type: smtp
    host: "smtp.example.com"
    port: 465
    security: ssl
    auth:
      mode: login
      username: "sender@example.com"
      password: "env:SMTP_PASSWORD"

  # Internal relay without authentication or encryption. Trusted networks only.
  smtp_relay:
    type: smtp
    host: "relay.internal.example"
    port: 25
    security: none
    auth:
      mode: none

  # Microsoft 365 SMTP for a signed-in user.
  # Get the token once with --init-oauth, then export MS_SMTP_REFRESH_TOKEN. The Entra app needs
  # the redirect URI http://localhost (platform "Mobile and desktop applications"). Without a
  # browser on this machine add "flow: device_code" (needs "Allow public client flows").
  microsoft_user:
    type: smtp
    host: "smtp.office365.com"
    port: 587
    security: starttls
    auth:
      mode: xoauth2
      username: "sender@example.com"
      oauth2:
        provider: microsoft
        grant: refresh_token
        tenant: "organizations"           # or your tenant GUID / domain
        client_id: "00000000-0000-0000-0000-000000000000"
        refresh_token: "env:MS_SMTP_REFRESH_TOKEN"

  # Microsoft 365 SMTP, application identity (client credentials, no interactive sign-in).
  # Needs SMTP.SendAsApp and Exchange service principal / mailbox permissions.
  microsoft_app:
    type: smtp
    host: "smtp.office365.com"
    port: 587
    security: starttls
    auth:
      mode: xoauth2
      username: "sender@example.com"
      oauth2:
        provider: microsoft
        grant: client_credentials
        tenant: "11111111-1111-1111-1111-111111111111"
        client_id: "22222222-2222-2222-2222-222222222222"
        client_secret: "env:MS_CLIENT_SECRET"

  # Cloudflare Email Sending over SMTP. The user name is the literal api_token; the password is an
  # API token with Email Sending: Edit permission.
  cloudflare_smtp:
    type: smtp
    host: "smtp.mx.cloudflare.net"
    port: 465
    security: ssl
    auth:
      mode: plain
      username: "api_token"
      password: "env:CF_API_TOKEN"

  # Cloudflare Email Sending over HTTPS (port 443), for networks that block SMTP.
  cloudflare_api:
    type: cloudflare_api
    account_id: "0123456789abcdef0123456789abcdef"
    api_token: "env:CF_API_TOKEN"

  # Microsoft Graph sendMail, application identity. Needs Graph Mail.Send (Application) + admin
  # consent; mailbox must equal message.from.
  microsoft_graph_app:
    type: microsoft_graph
    mailbox: "sender@example.com"
    oauth2:
      provider: microsoft
      grant: client_credentials
      tenant: "11111111-1111-1111-1111-111111111111"
      client_id: "22222222-2222-2222-2222-222222222222"
      client_secret: "env:MS_CLIENT_SECRET"

  # Microsoft Graph sendMail for a signed-in user. Its refresh token is separate from the SMTP one
  # because the scope differs.
  microsoft_graph_user:
    type: microsoft_graph
    oauth2:
      provider: microsoft
      grant: refresh_token
      tenant: "organizations"
      client_id: "00000000-0000-0000-0000-000000000000"
      refresh_token: "env:MS_GRAPH_REFRESH_TOKEN"

  # Gmail SMTP with XOAUTH2. Create an OAuth client of type "Desktop app", fill in client_id and
  # export GOOGLE_CLIENT_SECRET, run --init-oauth, then export GOOGLE_REFRESH_TOKEN.
  gmail_oauth:
    type: smtp
    host: "smtp.gmail.com"
    port: 465
    security: ssl
    auth:
      mode: xoauth2
      username: "sender@gmail.com"
      oauth2:
        provider: google
        grant: refresh_token
        client_id: "example.apps.googleusercontent.com"
        client_secret: "env:GOOGLE_CLIENT_SECRET"
        refresh_token: "env:GOOGLE_REFRESH_TOKEN"

  # Any other OAuth2 provider that supports the authorization code flow (for example a Yahoo,
  # Zoho or self-hosted identity server). Register http://localhost as redirect URI, or set
  # redirect_port to the exact port the provider has registered.
  generic_oauth:
    type: smtp
    host: "smtp.example.com"
    port: 587
    security: starttls
    auth:
      mode: xoauth2
      username: "sender@example.com"
      oauth2:
        provider: generic
        grant: refresh_token
        authorize_url: "https://login.example.com/oauth2/authorize"
        token_url: "https://login.example.com/oauth2/token"
        scope: "mail.send offline_access"
        client_id: "your-client-id"
        client_secret: "env:OAUTH_CLIENT_SECRET"      # remove for a public client
        # redirect_port: 8765
        refresh_token: "env:OAUTH_REFRESH_TOKEN"

  # Any SMTP server with a short-lived access token you obtained elsewhere.
  generic_xoauth2:
    type: smtp
    host: "smtp.example.com"
    port: 587
    security: starttls
    auth:
      mode: xoauth2
      username: "sender@example.com"
      oauth2:
        provider: generic
        grant: access_token
        access_token: "env:SMTP_OAUTH_ACCESS_TOKEN"
'@

# Only the main script is required. A portable YAML subset parser is included below.
# Supported: indented mappings, scalar lists, [inline, lists], quoted/plain scalars,
# true/false/null/integer values, # comments, and | / |- multiline strings.
# YAML tags, anchors, flow maps, and complex list objects are deliberately unsupported.
function Remove-ConfigInlineComment {
    param([string]$Text)
    $quote = [char]0
    for ($i = 0; $i -lt $Text.Length; $i++) {
        $ch = $Text[$i]
        if ($quote -eq [char]0) {
            if ($ch -eq [char]39 -or $ch -eq [char]34) { $quote = $ch; continue }
            if ($ch -eq [char]35 -and ($i -eq 0 -or [char]::IsWhiteSpace($Text[$i - 1]))) {
                return $Text.Substring(0, $i).TrimEnd()
            }
        } elseif ($quote -eq [char]39) {
            if ($ch -eq [char]39) {
                if ($i + 1 -lt $Text.Length -and $Text[$i + 1] -eq [char]39) { $i++ }
                else { $quote = [char]0 }
            }
        } else {
            if ($ch -eq [char]92) { $i++; continue }
            if ($ch -eq [char]34) { $quote = [char]0 }
        }
    }
    if ($quote -ne [char]0) { throw 'Unclosed quote in YAML scalar.' }
    return $Text.TrimEnd()
}

function ConvertFrom-ConfigInlineList {
    param([string]$Value)
    $inside = $Value.Substring(1, $Value.Length - 2)
    if ([string]::IsNullOrWhiteSpace($inside)) { return ,@() }
    $tokens = New-Object 'System.Collections.Generic.List[string]'
    $quote = [char]0
    $start = 0
    for ($i = 0; $i -lt $inside.Length; $i++) {
        $c = $inside[$i]
        if ($quote -eq [char]0) {
            if ($c -eq [char]39 -or $c -eq [char]34) { $quote = $c; continue }
            if ($c -eq [char]44) {
                $tokens.Add($inside.Substring($start, $i - $start).Trim())
                $start = $i + 1
            }
        } elseif ($quote -eq [char]39) {
            if ($c -eq [char]39) {
                if ($i + 1 -lt $inside.Length -and $inside[$i + 1] -eq [char]39) { $i++ }
                else { $quote = [char]0 }
            }
        } else {
            if ($c -eq [char]92) { $i++; continue }
            if ($c -eq [char]34) { $quote = [char]0 }
        }
    }
    if ($quote -ne [char]0) { throw 'Unclosed quote in YAML inline list.' }
    $tokens.Add($inside.Substring($start).Trim())
    $result = New-Object 'System.Collections.Generic.List[object]'
    foreach ($token in $tokens) {
        if ($token.Length -eq 0) { throw 'YAML inline list has an empty item (trailing comma is not supported).' }
        $result.Add((ConvertFrom-ConfigScalar $token))
    }
    return ,($result.ToArray())
}

function ConvertFrom-ConfigScalar {
    param([string]$Text)
    $value = (Remove-ConfigInlineComment $Text).Trim()
    if ($value.Length -eq 0) { return $null }
    if ($value.StartsWith('[') -and $value.EndsWith(']')) {
        $arrayValue = ConvertFrom-ConfigInlineList $value
        return ,$arrayValue
    }
    if ($value.StartsWith("'")) {
        if ($value.Length -lt 2 -or -not $value.EndsWith("'")) { throw 'Invalid YAML single-quoted string.' }
        $inner = $value.Substring(1, $value.Length - 2)
        if (($inner.Replace("''", '') -match "'")) { throw 'Invalid YAML single quote escape (use two single quotes).' }
        return $inner.Replace("''", "'")
    }
    if ($value.StartsWith('"')) {
        if ($value.Length -lt 2 -or -not $value.EndsWith('"')) { throw 'Invalid YAML double-quoted string.' }
        try { return [string](ConvertFrom-Json -InputObject $value -ErrorAction Stop) }
        catch { throw 'Invalid YAML double-quoted string. Use JSON-style escapes or single quotes.' }
    }
    if ($value -match '^(?:null|~)$') { return $null }
    if ($value -match '^(?i:true|false)$') { return ($value -ieq 'true') }
    if ($value -match '^-?(?:0|[1-9][0-9]*)$') {
        $number = [long]0
        if ([long]::TryParse($value, [ref]$number)) { return $number }
    }
    if ($value.StartsWith('{') -or $value.StartsWith('*') -or $value.StartsWith('!') -or $value.StartsWith('&')) {
        throw "Unsupported YAML syntax '$value'. The built-in parser supports only simple mappings and scalar lists."
    }
    return $value
}

function Get-NextConfigLine {
    param([string[]]$Lines, [ref]$Position)
    while ([int]$Position.Value -lt $Lines.Length) {
        $lineNo = [int]$Position.Value
        $line = $Lines[$lineNo]
        if ($line -match '^\s*$' -or $line -match '^\s*#') {
            $Position.Value = $lineNo + 1
            continue
        }
        if ($line -match '^\s*\t') { throw "YAML line $($lineNo + 1): use spaces instead of tabs for indentation." }
        if ($line -match '^\s*(?:---|\.\.\.)\s*$') { throw "YAML line $($lineNo + 1): multiple YAML documents are unsupported." }
        $indent = [regex]::Match($line, '^ *').Length
        return @{ Number = $lineNo + 1; Indent = $indent; Text = $line.Substring($indent) }
    }
    return $null
}

function Read-ConfigBlockScalar {
    param([string[]]$Lines, [ref]$Position, [int]$ParentIndent, [string]$Style)
    $raw = New-Object 'System.Collections.Generic.List[string]'
    $minimum = [int]::MaxValue
    while ([int]$Position.Value -lt $Lines.Length) {
        $line = $Lines[[int]$Position.Value]
        if ($line -notmatch '^\s*$') {
            if ($line -match '^\s*\t') { throw 'YAML block scalar indentation cannot contain tabs.' }
            $spaces = [regex]::Match($line, '^ *').Length
            if ($spaces -le $ParentIndent) { break }
            if ($spaces -lt $minimum) { $minimum = $spaces }
        }
        $raw.Add($line)
        $Position.Value = [int]$Position.Value + 1
    }
    if ($minimum -eq [int]::MaxValue) { return '' }
    $out = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in $raw) {
        if ($line -match '^\s*$') { $out.Add('') }
        else { $out.Add($line.Substring($minimum)) }
    }
    while ($out.Count -gt 0 -and $out[$out.Count - 1] -eq '') { $out.RemoveAt($out.Count - 1) }
    $joined = $out -join "`n"
    if ($Style -eq '|') { $joined += "`n" }
    return $joined
}

function Read-ConfigNode {
    param([string[]]$Lines, [ref]$Position, [int]$Indent)
    $first = Get-NextConfigLine -Lines $Lines -Position $Position
    if ($null -eq $first -or $first.Indent -ne $Indent) { throw 'Invalid YAML indentation.' }
    if ($first.Text -match '^-(?:\s|$)') {
        $items = New-Object 'System.Collections.Generic.List[object]'
        while ($true) {
            $line = Get-NextConfigLine -Lines $Lines -Position $Position
            if ($null -eq $line -or $line.Indent -lt $Indent) { break }
            if ($line.Indent -gt $Indent) { throw "YAML line $($line.Number): unexpected list indentation." }
            $itemMatch = [regex]::Match([string]$line.Text, '^-(?:\s+(.*))?$')
            if (-not $itemMatch.Success) {
                throw "YAML line $($line.Number): expected a list item '- value' at indent $Indent."
            }
            $body = [string]$itemMatch.Groups[1].Value
            $Position.Value = [int]$Position.Value + 1
            if ([string]::IsNullOrWhiteSpace($body)) {
                $look = Get-NextConfigLine -Lines $Lines -Position $Position
                if ($null -ne $look -and $look.Indent -gt $Indent) {
                    $items.Add((Read-ConfigNode -Lines $Lines -Position $Position -Indent $look.Indent))
                } else {
                    $items.Add($null)
                }
            } else {
                $items.Add((ConvertFrom-ConfigScalar $body))
            }
        }
        return ,($items.ToArray())
    }
    $map = [ordered]@{}
    while ($true) {
        $line = Get-NextConfigLine -Lines $Lines -Position $Position
        if ($null -eq $line -or $line.Indent -lt $Indent) { break }
        if ($line.Indent -gt $Indent) { throw "YAML line $($line.Number): unexpected indentation." }
        $matched = [regex]::Match([string]$line.Text, '^([A-Za-z_][A-Za-z0-9_.-]*)\s*:(?:[ \t]*(.*))?$')
        if (-not $matched.Success) {
            throw "YAML line $($line.Number): expected 'key: value' (complex YAML keys are not supported)."
        }
        $key = [string]$matched.Groups[1].Value
        if ($map.Contains($key)) { throw "YAML line $($line.Number): duplicate key '$key'." }
        $value = (Remove-ConfigInlineComment ([string]$matched.Groups[2].Value)).Trim()
        $Position.Value = [int]$Position.Value + 1
        if ($value -eq '|' -or $value -eq '|-') {
            $map[$key] = Read-ConfigBlockScalar -Lines $Lines -Position $Position -ParentIndent $Indent -Style $value
        } elseif ($value.Length -eq 0) {
            $next = Get-NextConfigLine -Lines $Lines -Position $Position
            if ($null -ne $next -and $next.Indent -gt $Indent) {
                $map[$key] = Read-ConfigNode -Lines $Lines -Position $Position -Indent $next.Indent
            } else {
                $map[$key] = $null
            }
        } else {
            $map[$key] = ConvertFrom-ConfigScalar $value
        }
    }
    return $map
}

function ConvertFrom-PortableYaml {
    param([string]$Text)
    $lines = $Text -split '\r\n|\n|\r'
    $index = 0
    $start = Get-NextConfigLine -Lines $lines -Position ([ref]$index)
    if ($null -eq $start) { throw 'The YAML config file is empty.' }
    if ($start.Indent -ne 0) { throw 'YAML root mappings must start at column 1.' }
    $config = Read-ConfigNode -Lines $lines -Position ([ref]$index) -Indent 0
    if ($config -isnot [System.Collections.IDictionary]) { throw 'YAML root must be a mapping (key: value).' }
    $tail = Get-NextConfigLine -Lines $lines -Position ([ref]$index)
    if ($null -ne $tail) { throw "YAML line $($tail.Number): unexpected trailing content." }
    return $config
}

function New-MailConfigFile {
    param([string]$Path)
    $folder = [IO.Path]::GetDirectoryName($Path)
    if (-not [IO.Directory]::Exists($folder)) {
        [void][IO.Directory]::CreateDirectory($folder)
    }
    # CreateNew guarantees that an existing configuration (and any secrets in it)
    # can never be overwritten, even if two processes start at the same time.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        $template = $script:EmbeddedMailConfig
        if (-not $template.EndsWith("`n")) { $template += $(if ($template.Contains("`r`n")) { "`r`n" } else { "`n" }) }
        try { $writer.Write($template); $writer.Flush() }
        finally { $writer.Dispose() }
    } finally {
        $stream.Dispose()
    }
}

try {
    # Default path is anchored at $PSScriptRoot, NOT at the current working directory.
    # Explicit relative -ConfigPath values are resolved against the PowerShell location (not the process cwd).
    $absoluteConfig = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ConfigPath)
    if (-not [IO.File]::Exists($absoluteConfig)) {
        New-MailConfigFile -Path $absoluteConfig
        Write-Host "[SETUP] Created example configuration: $absoluteConfig" -ForegroundColor Yellow
        Write-Host '        Edit message.from / message.to and the selected SMTP/API profile, then run the script again.'
        Write-Host '        No email was sent. (Exit code 2 = configuration created.)'
        exit 2
    }
    $yamlText = [IO.File]::ReadAllText($absoluteConfig, [Text.Encoding]::UTF8)
    $config = ConvertFrom-PortableYaml -Text $yamlText
    $profiles = Get-Setting $config 'profiles'
    if ($null -eq $profiles) { throw 'YAML must contain profiles: mapping.' }
    if (@($PSBoundParameters.Keys | Where-Object { $_ -ne 'ConfigPath' }).Count -eq 0 -and (Test-Interactive)) {
        $defaultName = Require-String $config 'default_profile' 'root'
        Write-Host 'mailtest - choose an action:'
        Write-Host "  1) Send a test mail (profile: $defaultName)"
        Write-Host '  2) Authorize an OAuth2 provider and get a refresh token'
        Write-Host '  q) Quit'
        if ((Read-Choice 'Select' 2) -eq 2) { $InitOAuth = [switch]$true }
    }
    if ($InitOAuth -and [string]::IsNullOrWhiteSpace($ProfileName) -and -not $ValidateOnly) {
        $ProfileName = Select-OAuthProvider -Profiles $profiles
    }
    if ([string]::IsNullOrWhiteSpace($ProfileName)) { $ProfileName = Require-String $config 'default_profile' 'root' }
    $selected = Get-Setting $profiles $ProfileName
    if ($null -eq $selected) { throw "Profile '$ProfileName' does not exist in YAML profiles." }
    $transport = Require-String $selected 'type' "profiles.$ProfileName"
    $messageSettings = Get-Setting $config 'message'
    if ($null -eq $messageSettings) { throw 'YAML must contain a message: mapping.' }
    if ($InitOAuth) {
        if ($ValidateOnly) { throw 'Choose -ValidateOnly or -InitOAuth, not both.' }
        if ($transport -eq 'cloudflare_api') { throw '-InitOAuth is not supported for Cloudflare.' }
        $initTarget = if ($transport -eq 'smtp') { 'smtp' } else { 'graph' }
        $oauthSection = $null
        if ($transport -eq 'smtp') {
            $smtpAuth = Get-Setting $selected 'auth'
            if ([string](Get-Setting $smtpAuth 'mode' 'none') -ne 'xoauth2') { throw '-InitOAuth requires smtp.auth.mode: xoauth2.' }
            $oauthSection = Get-Setting $smtpAuth 'oauth2'
        } else {
            $oauthSection = Get-Setting $selected 'oauth2'
        }
        Test-ProfileSettings -ProfileSettings $selected -Transport $transport -Mail @{ from = ''; to = @(); cc = @(); bcc = @() }
        if ([string](Get-Setting $oauthSection 'grant' 'refresh_token') -ne 'refresh_token') { throw '-InitOAuth requires oauth2.grant: refresh_token.' }
        Invoke-OAuthInit -OAuth $oauthSection -Target $initTarget
        exit 0
    }
    $overrideRecipients = @()
    if ($PSBoundParameters.ContainsKey('To')) { $overrideRecipients = @($To) }
    $message = Get-MailSettings -Settings $messageSettings -OverrideTo $overrideRecipients
    if ($PSBoundParameters.ContainsKey('Subject')) {
        if ($Subject -match '[\r\n]') { throw 'Subject must not contain CR/LF.' }
        Assert-MailSubject $Subject
        $message.subject = $Subject
    }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $message.text = $Body
        $message.html = ''
    }
    if ([string]::IsNullOrEmpty($message.text) -and [string]::IsNullOrEmpty($message.html)) {
        throw 'At least one of message.text or message.html must be present after overrides.'
    }
    Test-ProfileSettings -ProfileSettings $selected -Transport $transport -Mail $message
    if ($ValidateOnly) {
        Write-Host "[OK] YAML loaded; profile='$ProfileName'; type='$transport'; from='$($message.from)'; to=$($message.to.Count)."
        Write-Host '     Checked YAML, chosen profile and message fields. No secrets resolved; no network connection or authentication attempted.'
        exit 0
    }
    Write-Host "Testing profile '$ProfileName' ($transport) -> $($message.to -join ', ')"
    switch ($transport) {
        'smtp' { Send-SmtpMail -ProfileSettings $selected -Mail $message }
        'cloudflare_api' { Send-CloudflareMail -ProfileSettings $selected -Mail $message }
        'microsoft_graph' { Send-GraphMail -ProfileSettings $selected -Mail $message }
    }
    exit 0
} catch {
    # Keep diagnostic output concise; do not print request bodies, AUTH payloads, or API tokens.
    Write-Host ('[ERROR] ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
