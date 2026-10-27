#!/usr/bin/env bash
# mailtest.sh - send one test message with SMTP, Microsoft Graph or the Cloudflare REST API, and
# obtain OAuth2 refresh tokens to hand to LMail.
#
# Single file: copy it anywhere. It reads mailtest.config.yaml from its own directory and creates
# that file from an embedded template when it is missing, then carries on. It uses the
# same YAML as mailtest.ps1. Needs bash 4+, curl, openssl, awk and od; python3 is optional and lets
# --init-oauth receive the browser redirect on a local port (without it you paste the URL).
#
# Two entries: send a test mail (default; default_profile or --profile), or --init-oauth to
# authorize an OAuth2 provider. With no arguments in a terminal a menu offers both. --init-oauth
# lists the OAuth2 profiles to choose from, prints the authorization URL (it never opens a
# browser), receives the redirect on a local port and prints the refresh token.
#
# Exit codes: 0 accepted (or --validate-only ok), 1 error.
set -uo pipefail
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
CONFIG_PATH="$SCRIPT_DIR/mailtest.config.yaml"
PROFILE_NAME=""
OVERRIDE_TO=()
OVERRIDE_SUBJECT=""
OVERRIDE_BODY=""
HAS_SUBJECT=0
HAS_BODY=0
INIT_OAUTH=0
VALIDATE_ONLY=0
TRACE_SMTP=0

die() { printf '[ERROR] %s\n' "$*" >&2; exit 1; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }

usage() {
  cat <<'USAGE'
Usage: mailtest.sh [options]
  --config PATH            YAML file (default: mailtest.config.yaml beside this script)
  --profile NAME           profile to use instead of default_profile
  --to ADDRESS             override message.to (repeat, or comma separated)
  --subject TEXT           override message.subject
  --body TEXT              override message.text (and drop message.html)
  --validate-only          check the YAML, profile and message; no network
  --init-oauth             sign in through a local web server, print the refresh token, send nothing
  --verbose-protocol       show the SMTP conversation (AUTH data hidden)
  -h, --help               this help
Exit codes: 0 accepted, 1 error.
USAGE
}

# ---------------------------------------------------------------------------------------------
# Embedded first-run template. mailtest.ps1 carries the identical text; keep the two in sync.
# ---------------------------------------------------------------------------------------------
write_template() {
  sed -e '1d' -e '$d' <<'MAILTEST_TEMPLATE'
#BEGIN-TEMPLATE
# Mail Send Test configuration, shared by mailtest.ps1 and mailtest.sh
# -----------------------------------------------------------------------------
# This file is created next to the script on the first run. Edit message.* and the profile you
# want, save, and run again. Details and links: docs/MAILTEST.md.
#
#   ./mailtest.sh                                 .\mailtest.ps1                 (menu)
#   ./mailtest.sh --validate-only                 .\mailtest.ps1 -ValidateOnly
#   ./mailtest.sh --profile smtp_ssl              .\mailtest.ps1 -Profile smtp_ssl
#   ./mailtest.sh --init-oauth                    .\mailtest.ps1 -InitOAuth      (get a token)
#
# Two entries: "send a test mail" uses a profile below; "authorize" (--init-oauth / -InitOAuth)
# lists the OAuth profiles, signs a user in through a small web server on this machine, prints
# the refresh token (give it to LMail, or export it as the env: variable the profile names) and
# exits. It does not send mail and never opens a browser: open the printed URL yourself.
#
# Secrets: write env:NAME instead of the value and export NAME in your shell. Never commit real
# passwords, client secrets, API tokens or refresh tokens.
# All host names, ids and addresses below are placeholders; replace them. In a terminal the
# authorize entry asks for client_id / client_secret when the placeholder is still there.
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
  # ---------------------------------------------------------------------------
  # Plain SMTP with a user name and password
  # ---------------------------------------------------------------------------
  # Port 587 + STARTTLS. Host, user and password come from your mail provider. Many providers
  # (Gmail, Outlook.com, Yahoo, iCloud) require an app password instead of the account password:
  # create it in the account's security settings (it usually needs 2-step verification).
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

  # Port 465 with TLS from the first byte.
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

  # ---------------------------------------------------------------------------
  # Microsoft: sign in as a user (refresh token)
  # ---------------------------------------------------------------------------
  # Register one app for both profiles below (Microsoft Entra admin center, entra.microsoft.com):
  #   1. Entra ID > App registrations > New registration. Name it anything.
  #   2. Supported account types:
  #        common         "Accounts in any organizational directory and personal Microsoft accounts"
  #                       (work, school AND personal accounts; the right choice for most people)
  #        organizations  "Accounts in any organizational directory" (work or school accounts only)
  #        consumers      personal accounts only (set the same account type as for common)
  #        a tenant GUID or domain   "This organizational directory only" (one company)
  #      Put the matching value into "tenant:" below.
  #   3. Redirect URI (the callback): platform "Public client/native (mobile & desktop)",
  #      value  http://localhost   (loopback addresses accept any port, so the tool's random port
  #      works). It can also be added later under Authentication > Add a platform > Mobile and
  #      desktop applications.
  #   4. Copy "Application (client) ID" from the Overview page into "client_id:". No client secret
  #      is needed for this public client.
  #   5. API permissions > Add a permission: see each profile. Consent as your organisation requires.
  #   6. Only for "flow: device_code" (a machine without a browser or localhost access):
  #      Authentication > Allow public client flows = Yes; then redirect URI is not used.
  # Then run the authorize entry, choose the profile, open the URL, sign in, and export the token.

  # Microsoft 365 / Outlook.com SMTP for a signed-in user.
  # Permission: API permissions > Microsoft APIs > Office 365 Exchange Online (under "APIs my
  # organization uses" if not listed) > Delegated > SMTP.Send.
  # Work or school mailbox: SMTP AUTH must be allowed for it (Exchange admin center > Mailboxes >
  # Manage email apps > Authenticated SMTP). host smtp.office365.com.
  # Personal Outlook.com / Hotmail mailbox: use host smtp-mail.outlook.com and tenant common.
  # Token variable: export MS_SMTP_REFRESH_TOKEN
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
        tenant: "common"                  # common | organizations | consumers | tenant GUID / domain
        client_id: "00000000-0000-0000-0000-000000000000"
        refresh_token: "env:MS_SMTP_REFRESH_TOKEN"

  # Microsoft Graph sendMail (HTTPS, no SMTP port needed) for a signed-in user, work or personal.
  # Permission: API permissions > Microsoft Graph > Delegated permissions > Mail.Send.
  # Uses the same app registration as above or its own; the refresh token is separate from the
  # SMTP one because the scope differs. Graph sends as the signed-in user.
  # Token variable: export MS_GRAPH_REFRESH_TOKEN
  microsoft_graph_user:
    type: microsoft_graph
    oauth2:
      provider: microsoft
      grant: refresh_token
      tenant: "common"                    # common | organizations | consumers | tenant GUID / domain
      client_id: "00000000-0000-0000-0000-000000000000"
      refresh_token: "env:MS_GRAPH_REFRESH_TOKEN"

  # ---------------------------------------------------------------------------
  # Microsoft: application identity (client credentials, work or school tenant only)
  # ---------------------------------------------------------------------------
  # No user signs in, so there is no callback / redirect URI and no token to fetch: the tool
  # exchanges client_id + client_secret for an access token on every run. Personal accounts are
  # not supported. Setup in the Entra admin center:
  #   1. App registrations > New registration, account type "This organizational directory only".
  #      Leave the redirect URI empty.
  #   2. Overview: copy "Application (client) ID" -> client_id, "Directory (tenant) ID" -> tenant.
  #      The tenant must be that GUID or your domain, never common/organizations/consumers.
  #   3. Certificates & secrets > New client secret: copy the secret VALUE (shown once), not its
  #      ID, and export it:  export MS_CLIENT_SECRET=...
  #   4. API permissions > Add a permission > Application permissions, then "Grant admin consent":
  #      for Graph: Microsoft Graph > Mail.Send; for SMTP: Office 365 Exchange Online > SMTP.SendAsApp
  #      plus an Exchange service principal and mailbox permission (see docs/MAILTEST.md).
  #   5. Mail.Send application permission can send as ANY mailbox: limit it with an Exchange
  #      Application Access Policy (New-ApplicationAccessPolicy) to the mailboxes you need.

  # Microsoft Graph sendMail as the mailbox below. "mailbox" must equal message.from.
  microsoft_graph_app:
    type: microsoft_graph
    mailbox: "sender@example.com"
    oauth2:
      provider: microsoft
      grant: client_credentials
      tenant: "11111111-1111-1111-1111-111111111111"
      client_id: "22222222-2222-2222-2222-222222222222"
      client_secret: "env:MS_CLIENT_SECRET"

  # Microsoft 365 SMTP (smtp.office365.com) as the mailbox in "username".
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

  # ---------------------------------------------------------------------------
  # Google
  # ---------------------------------------------------------------------------
  # Gmail SMTP with XOAUTH2 (scope https://mail.google.com/). Setup in the Google Cloud console
  # (console.cloud.google.com):
  #   1. Create or select a project.
  #   2. APIs & Services > OAuth consent screen (Google Auth Platform): choose "External" (or
  #      "Internal" for a Google Workspace organisation) and fill the required fields. While the
  #      app is in "Testing" status add your Google account under "Test users"; in Testing,
  #      refresh tokens expire after 7 days. Publish the app for longer-lived tokens.
  #   3. APIs & Services > Credentials > Create credentials > OAuth client ID, application type
  #      "Desktop app". Copy "Client ID" -> client_id and "Client secret" ->
  #      export GOOGLE_CLIENT_SECRET=...
  #   4. Callback / redirect URI: nothing to register. A Desktop app client accepts
  #      http://localhost and http://127.0.0.1 on any port. If you use a "Web application"
  #      client instead, add  http://localhost:PORT  under "Authorized redirect URIs" and set
  #      redirect_port: PORT below.
  #   5. Run the authorize entry, choose gmail_oauth, open the URL, approve "Read, compose, send
  #      and permanently delete all your email from Gmail", then export GOOGLE_REFRESH_TOKEN.
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
        # redirect_port: 8765                 # only for a "Web application" client
        refresh_token: "env:GOOGLE_REFRESH_TOKEN"

  # ---------------------------------------------------------------------------
  # Any other OAuth2 provider (authorization code flow)
  # ---------------------------------------------------------------------------
  # Yahoo, Zoho, a self-hosted identity server, ... Take these from the provider's developer
  # documentation / app console:
  #   authorize_url, token_url   the OAuth2 authorization and token endpoints (HTTPS)
  #   scope                      what lets you send mail; include the offline scope that yields a
  #                              refresh token (often "offline_access")
  #   client_id / client_secret  from the app you register there (omit the secret for a public
  #                              client)
  #   callback / redirect URI    register  http://localhost  ; if the provider needs the exact URI,
  #                              register  http://localhost:8765  and set redirect_port: 8765
  # The provider must support authorization code with PKCE and return a refresh token.
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

  # Any SMTP server with a short-lived access token you obtained elsewhere (no refresh).
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

  # ---------------------------------------------------------------------------
  # Cloudflare Email Sending (no OAuth)
  # ---------------------------------------------------------------------------
  # Enable Email Sending for your account and verify the sender domain first
  # (Cloudflare dashboard > Email > Email Sending). Create an API token: My Profile > API Tokens >
  # Create Token > Custom token with permission  Account > Email Sending > Edit , then
  #   export CF_API_TOKEN=...
  # SMTP: the user name is the literal text api_token and the password is that token.
  cloudflare_smtp:
    type: smtp
    host: "smtp.mx.cloudflare.net"
    port: 465
    security: ssl
    auth:
      mode: plain
      username: "api_token"
      password: "env:CF_API_TOKEN"

  # HTTPS API (port 443), for networks that block SMTP. The 32-character account id is shown on
  # the dashboard (Account home > the account's menu > Copy account ID, or in the URL
  # dash.cloudflare.com/<account id>/...).
  cloudflare_api:
    type: cloudflare_api
    account_id: "0123456789abcdef0123456789abcdef"
    api_token: "env:CF_API_TOKEN"
#END-TEMPLATE
MAILTEST_TEMPLATE
}

# ---------------------------------------------------------------------------------------------
# YAML subset -> flat lines "path<TAB>type<TAB>value" (type: s string, b bool, i int, n null).
# Values escape "\" as "\\" and newline as "\n". Same subset and messages as mailtest.ps1.
# ---------------------------------------------------------------------------------------------
yaml_flatten() {
  awk '
function fail(msg) { if (msg ~ /^line /) msg = "YAML " msg; print "YAML: " msg > "/dev/stderr"; exit 1 }
function trim(s) { sub(/^[ \t]+/, "", s); sub(/[ \t]+$/, "", s); return s }
function esc(s) { gsub(/\\/, "\\\\", s); gsub(/\n/, "\\n", s); return s }
function emit(path, type, value) { print path "\t" type "\t" esc(value) }

# index of the next significant line >= i, or 0. Sets IND and TXT.
function skip(i,   line) {
  while (i <= n) {
    line = L[i]
    if (line ~ /^[ \t]*$/ || line ~ /^[ \t]*#/) { i++; continue }
    if (line ~ /^[ \t]*\t/) fail("line " i ": use spaces instead of tabs for indentation.")
    if (line ~ /^[ \t]*(---|\.\.\.)[ \t]*$/) fail("line " i ": multiple YAML documents are unsupported.")
    match(line, /^ */); IND = RLENGTH; TXT = substr(line, IND + 1)
    return i
  }
  return 0
}

function strip_comment(t,   i, c, q, len) {
  q = ""; len = length(t)
  for (i = 1; i <= len; i++) {
    c = substr(t, i, 1)
    if (q == "") {
      if (c == "\"" || c == "\047") { q = c; continue }
      if (c == "#" && (i == 1 || substr(t, i - 1, 1) ~ /[ \t]/)) return trim(substr(t, 1, i - 1))
    } else if (q == "\047") {
      if (c == "\047") { if (substr(t, i + 1, 1) == "\047") i++; else q = "" }
    } else {
      if (c == "\\") { i++; continue }
      if (c == "\"") q = ""
    }
  }
  if (q != "") fail("Unclosed quote in YAML scalar.")
  return trim(t)
}

function double_quoted(v,   out, i, c, d, len) {
  len = length(v); out = ""
  for (i = 2; i < len; i++) {
    c = substr(v, i, 1)
    if (c == "\"") fail("Invalid YAML double-quoted string. Use JSON-style escapes or single quotes.")
    if (c == "\\") {
      i++; d = substr(v, i, 1)
      if (d == "n") out = out "\n"; else if (d == "t") out = out "\t"; else if (d == "r") out = out "\r"
      else if (d == "b") out = out "\b"; else if (d == "f") out = out "\f"
      else if (d == "\"" || d == "\\" || d == "/") out = out d
      else fail("Invalid YAML double-quoted string. Use JSON-style escapes or single quotes.")
    } else out = out c
  }
  return out
}

# Emits one scalar under path (inline lists become path.0, path.1, ...). inlist forbids nesting.
function scalar(path, text, inlist,   v, inner, i, c, q, start, tok, k, cnt, parts, rest) {
  v = strip_comment(text)
  if (v == "") { emit(path, "n", ""); return }
  if (substr(v, 1, 1) == "[" && substr(v, length(v), 1) == "]") {
    if (inlist) fail("Nested YAML lists are not supported.")
    inner = substr(v, 2, length(v) - 2)
    if (trim(inner) == "") return
    q = ""; start = 1; cnt = 0
    for (i = 1; i <= length(inner); i++) {
      c = substr(inner, i, 1)
      if (q == "") {
        if (c == "\"" || c == "\047") { q = c; continue }
        if (c == ",") { parts[cnt++] = trim(substr(inner, start, i - start)); start = i + 1 }
      } else if (q == "\047") {
        if (c == "\047") { if (substr(inner, i + 1, 1) == "\047") i++; else q = "" }
      } else {
        if (c == "\\") { i++; continue }
        if (c == "\"") q = ""
      }
    }
    if (q != "") fail("Unclosed quote in YAML inline list.")
    parts[cnt++] = trim(substr(inner, start))
    for (k = 0; k < cnt; k++) {
      if (parts[k] == "") fail("YAML inline list has an empty item (trailing comma is not supported).")
      scalar(path "." k, parts[k], 1)
    }
    return
  }
  c = substr(v, 1, 1)
  if (c == "\047") {
    if (length(v) < 2 || substr(v, length(v), 1) != "\047") fail("Invalid YAML single-quoted string.")
    inner = substr(v, 2, length(v) - 2)
    rest = inner; gsub(/\047\047/, "", rest)
    if (index(rest, "\047") > 0) fail("Invalid YAML single quote escape (use two single quotes).")
    gsub(/\047\047/, "\047", inner)
    emit(path, "s", inner); return
  }
  if (c == "\"") {
    if (length(v) < 2 || substr(v, length(v), 1) != "\"") fail("Invalid YAML double-quoted string.")
    emit(path, "s", double_quoted(v)); return
  }
  if (v ~ /^(null|~)$/) { emit(path, "n", ""); return }
  if (tolower(v) == "true" || tolower(v) == "false") { emit(path, "b", tolower(v)); return }
  if (v ~ /^-?(0|[1-9][0-9]*)$/) { emit(path, "i", v); return }
  if (c == "{" || c == "*" || c == "!" || c == "&")
    fail("Unsupported YAML syntax \047" v "\047. The built-in parser supports only simple mappings and scalar lists.")
  emit(path, "s", v)
}

# Parses the block scalar that starts at line i; returns the next line index.
function block_scalar(path, i, parent, style,   minimum, spaces, start, j, out, cnt, text) {
  minimum = 1000000; start = i
  while (i <= n) {
    if (L[i] !~ /^[ \t]*$/) {
      if (L[i] ~ /^[ \t]*\t/) fail("YAML block scalar indentation cannot contain tabs.")
      match(L[i], /^ */); spaces = RLENGTH
      if (spaces <= parent) break
      if (spaces < minimum) minimum = spaces
    }
    i++
  }
  if (minimum == 1000000) { emit(path, "s", ""); return i }
  cnt = 0
  for (j = start; j < i; j++) out[cnt++] = (L[j] ~ /^[ \t]*$/) ? "" : substr(L[j], minimum + 1)
  while (cnt > 0 && out[cnt - 1] == "") cnt--
  text = ""
  for (j = 0; j < cnt; j++) text = text (j ? "\n" : "") out[j]
  if (style == "|") text = text "\n"
  emit(path, "s", text)
  return i
}

# Parses the node at line i (a list or a map at indent); returns the next line index.
function node(i, indent, path,   j, body, look, key, rest, value, full, idx, headline) {
  i = skip(i)
  if (!i || IND != indent) fail("Invalid YAML indentation.")
  if (TXT ~ /^-([ \t]|$)/) {
    idx = 0
    while (1) {
      j = skip(i)
      if (!j || IND < indent) break
      if (IND > indent) fail("line " j ": unexpected list indentation.")
      if (TXT !~ /^-([ \t]+.*)?$/) fail("line " j ": expected a list item \047- value\047 at indent " indent ".")
      body = trim(substr(TXT, 2))
      i = j + 1
      if (body == "" || body ~ /^#/) {
        look = skip(i)
        if (look && IND > indent) fail("line " j ": list items must be scalars.")
        emit(path "." idx, "n", "")
      } else scalar(path "." idx, body, 0)
      idx++
    }
    return i
  }
  while (1) {
    j = skip(i)
    if (!j || IND < indent) break
    if (IND > indent) fail("line " j ": unexpected indentation.")
    if (!match(TXT, /^[A-Za-z_][A-Za-z0-9_.-]*[ \t]*:/)) fail("line " j ": expected \047key: value\047 (complex YAML keys are not supported).")
    key = substr(TXT, 1, RLENGTH - 1); sub(/[ \t]+$/, "", key)
    rest = substr(TXT, RLENGTH + 1); sub(/^[ \t]+/, "", rest)
    full = (path == "" ? key : path "." key)
    if (full in SEEN) fail("line " j ": duplicate key \047" key "\047.")
    SEEN[full] = 1
    value = strip_comment(rest)
    i = j + 1
    if (value == "|" || value == "|-") i = block_scalar(full, i, indent, value)
    else if (value == "") {
      look = skip(i)
      if (look && IND > indent) i = node(look, IND, full)
      else emit(full, "n", "")
    } else scalar(full, value, 0)
  }
  return i
}

{ sub(/\r$/, ""); L[++n] = $0 }
END {
  start = skip(1)
  if (!start) fail("The YAML config file is empty.")
  if (IND != 0) fail("YAML root mappings must start at column 1.")
  if (TXT ~ /^-([ \t]|$)/) fail("YAML root must be a mapping (key: value).")
  next_line = node(start, 0, "")
  tail = skip(next_line)
  if (tail) fail("line " tail ": unexpected trailing content.")
}
' "$1"
}

# ---------------------------------------------------------------------------------------------
# Config access. CFG is the flattened file. Lookups set CT (type) and CV (value) or REPLY.
# ---------------------------------------------------------------------------------------------
cfg_get() {
  local line raw
  line="$(awk -F'\t' -v k="$1" '$1 == k { print $2 "\t" $3; exit }' "$CFG")"
  if [ -z "$line" ]; then CT=""; CV=""; return 1; fi
  CT="${line%%$'\t'*}"
  raw="${line#*$'\t'}"
  printf -v CV '%b' "$raw"
}

# cfg_str KEY [DEFAULT]: value, or DEFAULT when missing or null.
cfg_str() {
  if cfg_get "$1" && [ "$CT" != n ]; then REPLY="$CV"; else REPLY="${2-}"; fi
}

cfg_has_children() {
  awk -F'\t' -v p="$1." 'index($1, p) == 1 { found = 1; exit } END { exit !found }' "$CFG"
}

cfg_exists() { cfg_has_children "$1" || { cfg_get "$1" && [ "$CT" != n ]; }; }

# cfg_list KEY: fills LIST with a scalar or a list under KEY.
cfg_list() {
  local item raw
  LIST=()
  if cfg_get "$1"; then
    [ "$CT" = n ] || LIST+=("$CV")
    return 0
  fi
  while IFS= read -r raw; do
    printf -v item '%b' "$raw"
    LIST+=("$item")
  done < <(awk -F'\t' -v p="$1." 'index($1, p) == 1 && substr($1, length(p) + 1) ~ /^[0-9]+$/ && $2 != "n" { print $3 }' "$CFG")
}

# resolve_secret VALUE NAME: env:NAME references are read from the environment.
resolve_secret() {
  local value="$1" name="$2" var
  [ -n "${value//[[:space:]]/}" ] || die "Missing credential: $name"
  if [[ "$value" =~ ^env:([A-Za-z_][A-Za-z0-9_]*)$ ]]; then
    var="${BASH_REMATCH[1]}"
    [ -n "${!var-}" ] || die "Environment variable '$var' is not set (required by $name)."
    REPLY="${!var}"
  else
    REPLY="$value"
  fi
}

require_string() {
  cfg_str "$1"
  [ -n "${REPLY//[[:space:]]/}" ] || die "Missing '$2.$3' in YAML."
}

# ---------------------------------------------------------------------------------------------
# Message settings and validation (same rules and messages as mailtest.ps1).
# ---------------------------------------------------------------------------------------------
check_mail_address() {
  local address="$1" name="$2"
  local pattern="^[A-Za-z0-9.!#\$%&'*+/=?^_\`{|}~-]+@[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?\$"
  [ -n "${address//[[:space:]]/}" ] && [[ "$address" != *$'\n'* && "$address" != *$'\r'* ]] || die "Invalid email address in $name."
  if ! [[ "$address" =~ ^[[:graph:]]+$ ]] || ! [[ "$address" =~ $pattern ]]; then
    die "Use a plain ASCII email address (without display name) for $name: $address"
  fi
}

assert_subject() {
  if [[ "$1" =~ [[:cntrl:]] ]]; then die 'Email subject must not contain control characters or line breaks.'; fi
}

load_message() {
  local item
  require_string message.from message from
  M_FROM="$REPLY"
  check_mail_address "$M_FROM" message.from

  M_TO=()
  if [ "${#OVERRIDE_TO[@]}" -gt 0 ]; then
    for item in "${OVERRIDE_TO[@]}"; do M_TO+=("$item"); done
  else
    cfg_list message.to
    for item in "${LIST[@]+"${LIST[@]}"}"; do M_TO+=("$item"); done
  fi
  for item in "${M_TO[@]+"${M_TO[@]}"}"; do check_mail_address "$item" message.to; done
  [ "${#M_TO[@]}" -gt 0 ] || die 'At least one recipient is required (message.to or --to).'

  M_CC=(); M_BCC=()
  cfg_list message.cc
  for item in "${LIST[@]+"${LIST[@]}"}"; do [ -n "$item" ] && { check_mail_address "$item" message.cc; M_CC+=("$item"); }; done
  cfg_list message.bcc
  for item in "${LIST[@]+"${LIST[@]}"}"; do [ -n "$item" ] && { check_mail_address "$item" message.bcc; M_BCC+=("$item"); }; done

  cfg_str message.subject '[SMTP Test] Connectivity check'; M_SUBJECT="$REPLY"
  assert_subject "$M_SUBJECT"
  cfg_str message.text 'This is a test email.'; M_TEXT="$REPLY"
  cfg_str message.html ''; M_HTML="$REPLY"
  if [ -z "$M_TEXT" ] && [ -z "$M_HTML" ]; then die 'At least one of message.text or message.html is required.'; fi
}

test_oauth_settings() {
  local prefix="$1" target="$2" provider grant tenant="" url key flow port
  cfg_exists "$prefix" && ! { cfg_get "$prefix" && [ "$CT" != n ]; } || die 'oauth2 must be a YAML mapping.'
  cfg_str "$prefix.provider" microsoft; provider="$REPLY"
  cfg_str "$prefix.grant" refresh_token; grant="$REPLY"
  case "$provider" in microsoft|google|generic) ;; *) die "Unsupported oauth2.provider: $provider" ;; esac
  if [ "$target" = graph ] && [ "$provider" != microsoft ]; then die 'Microsoft Graph requires oauth2.provider: microsoft.'; fi
  [ "$grant" != delegated ] || die 'oauth2.grant: delegated was removed; use grant: refresh_token (see docs/MAILTEST.md).'
  case "$grant" in access_token|client_credentials|refresh_token) ;; *) die "Unsupported oauth2.grant: $grant" ;; esac
  if [ "$grant" = access_token ]; then
    require_string "$prefix.access_token" oauth2 access_token
    return 0
  fi
  require_string "$prefix.client_id" oauth2 client_id
  if [ "$provider" = microsoft ]; then
    cfg_str "$prefix.tenant" common; tenant="$REPLY"
    [[ "$tenant" =~ ^[A-Za-z0-9.-]+$ ]] || die 'Invalid oauth2.tenant.'
  fi
  cfg_str "$prefix.flow" loopback; flow="$REPLY"
  case "$flow" in loopback|device_code) ;; *) die 'oauth2.flow must be loopback or device_code.' ;; esac
  if [ "$flow" = device_code ] && [ "$provider" != microsoft ]; then die 'oauth2.flow: device_code supports Microsoft only.'; fi
  if cfg_get "$prefix.redirect_port" && [ "$CT" != n ]; then
    port="$CV"
    if ! [[ "$port" =~ ^[0-9]+$ ]] || [ "$port" -lt 1024 ] || [ "$port" -gt 65535 ]; then die 'oauth2.redirect_port must be a number from 1024 to 65535.'; fi
  fi
  if [ "$provider" = generic ]; then
    for key in token_url authorize_url; do
      cfg_str "$prefix.$key" ''; url="$REPLY"
      if [ -z "${url//[[:space:]]/}" ]; then
        [ "$key" != token_url ] || die "Missing 'oauth2.token_url' in YAML."
        continue
      fi
      [[ "$url" =~ ^https://[^/@]+(/.*)?$ ]] || die "oauth2.$key must be an HTTPS URL without embedded credentials."
    done
  fi
  if [ "$grant" = client_credentials ]; then
    [ "$provider" = microsoft ] || die 'client_credentials currently supports Microsoft only.'
    case "$tenant" in common|organizations|consumers) die 'Microsoft client_credentials requires a tenant GUID or tenant domain.' ;; esac
    require_string "$prefix.client_secret" oauth2 client_secret
  else
    require_string "$prefix.refresh_token" oauth2 refresh_token
  fi
}

test_profile_settings() {
  local prefix="$1" transport="$2" host port security timeout mode user id mailbox oauth_grant
  case "$transport" in
    smtp)
      require_string "$prefix.host" smtp host; host="$REPLY"
      if [[ "$host" =~ [[:space:][:cntrl:]] ]]; then die 'smtp.host must not contain spaces or control characters.'; fi
      cfg_str "$prefix.port" 587; port="$REPLY"
      if ! [[ "$port" =~ ^[0-9]+$ ]] || [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then die 'smtp.port must be a number from 1 to 65535.'; fi
      cfg_str "$prefix.security" starttls; security="$REPLY"
      case "$security" in starttls|ssl|none) ;; *) die 'smtp.security must be starttls, ssl, or none.' ;; esac
      cfg_str "$prefix.timeout_seconds" 25; timeout="$REPLY"
      if ! [[ "$timeout" =~ ^[0-9]+$ ]] || [ "$timeout" -lt 1 ] || [ "$timeout" -gt 300 ]; then die 'smtp.timeout_seconds must be an integer from 1 to 300.'; fi
      ALLOW_INSECURE=0
      if cfg_get "$prefix.allow_insecure_auth" && [ "$CT" != n ]; then
        [ "$CT" = b ] || die 'smtp.allow_insecure_auth must be true or false (unquoted YAML boolean).'
        [ "$CV" != true ] || ALLOW_INSECURE=1
      fi
      if cfg_get "$prefix.auth" && [ "$CT" != n ]; then die 'smtp.auth must be a YAML mapping.'; fi
      cfg_str "$prefix.auth.mode" none; mode="$REPLY"
      case "$mode" in none|auto|plain|login|xoauth2) ;; *) die 'smtp.auth.mode must be none, auto, plain, login, or xoauth2.' ;; esac
      if [ "$security" = none ] && [ "$mode" != none ] && [ "$ALLOW_INSECURE" -eq 0 ]; then
        die 'Refusing to send SMTP credentials without TLS. To override, set allow_insecure_auth: true.'
      fi
      if [ "$mode" != none ]; then
        require_string "$prefix.auth.username" smtp.auth username; user="$REPLY"
        if [[ "$user" == *$'\n'* || "$user" == *$'\r'* ]]; then die 'SMTP username must not contain NUL or line breaks.'; fi
        if [ "$mode" = xoauth2 ]; then
          test_oauth_settings "$prefix.auth.oauth2" smtp
        else
          require_string "$prefix.auth.password" smtp.auth password
        fi
      fi
      ;;
    cloudflare_api)
      require_string "$prefix.account_id" cloudflare_api account_id; id="$REPLY"
      [[ "$id" =~ ^[a-fA-F0-9]{32}$ ]] || die 'cloudflare_api.account_id must be a 32-character hexadecimal ID.'
      require_string "$prefix.api_token" cloudflare_api api_token
      if [ $(( ${#M_TO[@]} + ${#M_CC[@]} + ${#M_BCC[@]} )) -gt 50 ]; then
        die 'Cloudflare Email Sending supports at most 50 combined To/Cc/Bcc recipients.'
      fi
      ;;
    microsoft_graph)
      test_oauth_settings "$prefix.oauth2" graph
      cfg_str "$prefix.oauth2.grant" refresh_token; oauth_grant="$REPLY"
      if [ "$oauth_grant" = client_credentials ]; then
        cfg_str "$prefix.mailbox" "$M_FROM"; mailbox="$REPLY"
        check_mail_address "$mailbox" microsoft_graph.mailbox
        [ "$mailbox" = "$M_FROM" ] || die 'microsoft_graph.mailbox must equal message.from for app authentication.'
      fi
      ;;
    *) die "Unsupported profile type: $transport" ;;
  esac
}

# ---------------------------------------------------------------------------------------------
# MIME message (ASCII headers, base64 bodies, CRLF). Bcc is never written; it is envelope only.
# ---------------------------------------------------------------------------------------------
base64_lines() { # stdin -> base64 wrapped at 76 columns, CRLF line ends
  openssl base64 -A | fold -w 76 | awk '{ printf "%s\r\n", $0 }'
}

body_base64() { # text -> canonical CRLF text as wrapped base64
  printf '%s' "$1" | awk '{ sub(/\r$/, ""); printf "%s\r\n", $0 }' | base64_lines
}

# Plain short ASCII stays as is; anything else becomes RFC 2047 encoded words of <= 42 UTF-8 bytes.
encode_header() {
  local text="$1" b v cur="" chunk="" count=0 c n escaped
  local -a bytes chars=() words=()
  if [[ "$text" =~ ^[[:print:]]*$ ]] && [ "${#text}" -le 66 ]; then REPLY="$text"; return; fi
  # shellcheck disable=SC2207
  bytes=($(printf '%s' "$text" | od -An -v -tx1))
  for b in "${bytes[@]}"; do
    v=$((16#$b))
    if [ "$v" -lt 128 ] || [ "$v" -ge 192 ]; then
      [ -z "$cur" ] || chars+=("$cur")
      cur="$b"
    else
      cur+=" $b"
    fi
  done
  [ -z "$cur" ] || chars+=("$cur")
  for c in "${chars[@]}"; do
    # shellcheck disable=SC2086
    set -- $c; n=$#
    if [ "$count" -gt 0 ] && [ $((count + n)) -gt 42 ]; then words+=("$chunk"); chunk=""; count=0; fi
    chunk+=" $c"; count=$((count + n))
  done
  [ -z "$chunk" ] || words+=("$chunk")
  REPLY=""
  for c in "${words[@]}"; do
    escaped="$(printf '\\x%s' $c)"
    [ -z "$REPLY" ] || REPLY+=$'\r\n '
    REPLY+="=?UTF-8?B?$(printf '%b' "$escaped" | openssl base64 -A)?="
  done
}

# address_header NAME addr...: folds at commas, keeping each address whole.
address_header() {
  local name="$1" line="$1: " first=1 segment address
  shift
  REPLY=""
  for address in "$@"; do
    if [ "$first" -eq 1 ]; then segment="$address"; else segment=", $address"; fi
    if [ "$first" -eq 0 ] && [ $((${#line} + ${#segment})) -gt 76 ]; then
      REPLY+="$line,"$'\r\n'; line=" $address"
    else
      line+="$segment"
    fi
    first=0
  done
  REPLY+="$line"
}

build_mime() { # writes the message to $1
  local out="$1" boundary date_header
  date_header="$(LC_ALL=C date '+%a, %d %b %Y %H:%M:%S %z')"
  encode_header "$M_SUBJECT"
  {
    printf 'Date: %s\r\n' "$date_header"
    printf 'From: %s\r\n' "$M_FROM"
    address_header To "${M_TO[@]}"; printf '%s\r\n' "$REPLY"
    printf 'Subject: %s\r\n' "$(encode_header "$M_SUBJECT"; printf '%s' "$REPLY")"
    printf 'MIME-Version: 1.0\r\nX-Mailer: mailtest.sh\r\n'
    if [ "${#M_CC[@]}" -gt 0 ]; then address_header Cc "${M_CC[@]}"; printf '%s\r\n' "$REPLY"; fi
    if [ -n "$M_HTML" ] && [ -n "$M_TEXT" ]; then
      boundary="=_TestMail_$(openssl rand -hex 16)"
      printf 'Content-Type: multipart/alternative; boundary="%s"\r\n\r\n' "$boundary"
      printf -- '--%s\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n' "$boundary"
      body_base64 "$M_TEXT"
      printf -- '--%s\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n' "$boundary"
      body_base64 "$M_HTML"
      printf -- '--%s--\r\n' "$boundary"
    else
      if [ -n "$M_HTML" ]; then
        printf 'Content-Type: text/html; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n'
        body_base64 "$M_HTML"
      else
        printf 'Content-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n'
        body_base64 "$M_TEXT"
      fi
    fi
  } > "$out"
}

# ---------------------------------------------------------------------------------------------
# HTTP helpers (curl). Secrets go through a curl config on stdin, never on the command line.
# ---------------------------------------------------------------------------------------------
curl_quote() { # value -> double-quoted curl config string
  local s="$1"
  s="${s//\\/\\\\}"; s="${s//\"/\\\"}"; s="${s//$'\n'/\\n}"; s="${s//$'\r'/\\r}"; s="${s//$'\t'/\\t}"
  printf '"%s"' "$s"
}

json_string() { # value -> JSON string literal
  local s="$1"
  s="${s//\\/\\\\}"; s="${s//\"/\\\"}"; s="${s//$'\n'/\\n}"; s="${s//$'\r'/\\r}"; s="${s//$'\t'/\\t}"
  printf '"%s"' "$s"
}

# json_field NAME BODY: first string or number value of "NAME" (flat provider responses only).
json_field() {
  printf '%s' "$2" | tr -d '\n' | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\\([^\"]*\\)\".*/\\1/p;t;s/.*\"$1\"[[:space:]]*:[[:space:]]*\\([0-9][0-9]*\\).*/\\1/p" | head -n 1 | sed 's#\\/#/#g'
}

urlencode() {
  local s="$1" i c out=""
  for ((i = 0; i < ${#s}; i++)); do
    c="${s:i:1}"
    case "$c" in [A-Za-z0-9._~-]) out+="$c" ;; *) out+="$(printf '%%%02X' "'$c")" ;; esac
  done
  printf '%s' "$out"
}

# http_call URL [curl args...]: HTTP_CFG (config text) is read from stdin; sets HTTP_CODE, HTTP_BODY.
HTTP_CFG=""
http_call() {
  local out
  if ! out="$(printf '%s\n' "$HTTP_CFG" | curl --silent --show-error --max-time 60 -K - -w $'\n%{http_code}' "$@" 2>"$TMP/curl.err")"; then
    HTTP_CODE=0; HTTP_BODY="$(tr '\n' ' ' < "$TMP/curl.err")"; return 1
  fi
  HTTP_CODE="${out##*$'\n'}"; HTTP_BODY="${out%$'\n'*}"
}

oauth_error() { # response body -> "error (description)"
  local err desc
  err="$(json_field error "$HTTP_BODY")"; desc="$(json_field error_description "$HTTP_BODY" | cut -c1-300)"
  if [ -n "$err" ] && [ -n "$desc" ]; then printf '%s (%s)' "$err" "$desc"; else printf '%s' "${err:-http_error}"; fi
}

# ---------------------------------------------------------------------------------------------
# OAuth2: token acquisition. Sets ACCESS_TOKEN.
# ---------------------------------------------------------------------------------------------
post_form() { # URL name=value... (values may be secrets)
  local url="$1" pair
  shift
  HTTP_CFG=""
  for pair in "$@"; do HTTP_CFG+="data-urlencode = $(curl_quote "$pair")"$'\n'; done
  http_call "$url"
}

microsoft_device_code() { # tenant client_id scope -> sets TOKEN_JSON
  local root="https://login.microsoftonline.com/$1/oauth2/v2.0" device interval expires deadline now
  post_form "$root/devicecode" "client_id=$2" "scope=$3" || die "Device-code request failed: $HTTP_BODY"
  device="$(json_field device_code "$HTTP_BODY")"
  [ "$HTTP_CODE" = 200 ] && [ -n "$device" ] || die "Device-code request failed: $(oauth_error)"
  printf 'Microsoft sign-in required (one-time bootstrap):\n%s\n' "$(json_field message "$HTTP_BODY")"
  interval="$(json_field interval "$HTTP_BODY")"; interval="${interval:-5}"; [ "$interval" -ge 5 ] || interval=5
  expires="$(json_field expires_in "$HTTP_BODY")"; deadline=$(( $(date +%s) + ${expires:-900} ))
  while now="$(date +%s)"; [ "$now" -lt "$deadline" ]; do
    sleep "$interval"
    post_form "$root/token" 'grant_type=urn:ietf:params:oauth:grant-type:device_code' "client_id=$2" "device_code=$device" \
      || die "Device-code polling failed: $HTTP_BODY"
    if [ "$HTTP_CODE" = 200 ]; then TOKEN_JSON="$HTTP_BODY"; return 0; fi
    case "$(json_field error "$HTTP_BODY")" in
      authorization_pending) ;;
      slow_down) interval=$((interval + 5)) ;;
      authorization_declined) die 'Device-code sign-in was declined.' ;;
      expired_token) die 'Device-code sign-in expired. Run --init-oauth again.' ;;
      *) die "Device-code authorization failed: $(oauth_error)" ;;
    esac
  done
  die 'Device-code sign-in timed out.'
}

pkce_challenge() { # verifier -> challenge
  printf '%s' "$1" | openssl dgst -sha256 -binary | openssl base64 -A | tr '+/' '-_' | tr -d '='
}

# authorization_code PASTED STATE -> sets REPLY to the code
authorization_code() {
  local pasted="$1" state="$2" query part key value code="" got_state="" err=""
  pasted="${pasted#"${pasted%%[![:space:]]*}"}"; pasted="${pasted%"${pasted##*[![:space:]]}"}"
  if ! [[ "$pasted" =~ ^https?:// ]]; then
    [ -n "$pasted" ] || die 'No authorization code was provided.'
    REPLY="$pasted"; return
  fi
  query="${pasted#*\?}"; [ "$query" != "$pasted" ] || query=""
  query="${query%%#*}"
  while IFS= read -r part; do
    [ -n "$part" ] || continue
    key="${part%%=*}"; value=""; [[ "$part" == *=* ]] && value="${part#*=}"
    value="${value//+/ }"; value="$(printf '%b' "${value//%/\\x}")"
    case "$key" in code) code="$value" ;; state) got_state="$value" ;; error) err="$value" ;; esac
  done < <(printf '%s\n' "$query" | tr '&' '\n')
  [ -z "$err" ] || die "Authorization failed: $err"
  [ "$got_state" = "$state" ] || die 'The state value in the pasted URL does not match this sign-in attempt.'
  [ -n "$code" ] || die 'The pasted URL has no code parameter.'
  REPLY="$code"
}

# load_oauth_settings PREFIX TARGET: endpoints, scope and flow of one oauth2 mapping into O_*.
load_oauth_settings() {
  local prefix="$1" target="$2"
  cfg_str "$prefix.provider" microsoft; O_PROVIDER="$REPLY"
  cfg_str "$prefix.tenant" common; O_TENANT="$REPLY"
  require_string "$prefix.client_id" oauth2 client_id; O_CLIENT_ID="$REPLY"
  cfg_str "$prefix.client_secret" ''; O_CLIENT_SECRET="$REPLY"
  cfg_str "$prefix.scope" ''; O_SCOPE="$REPLY"
  cfg_str "$prefix.flow" loopback; O_FLOW="$REPLY"
  cfg_str "$prefix.redirect_port" 0; O_PORT="$REPLY"
  cfg_str "$prefix.grant" refresh_token; local grant="$REPLY"
  O_AUTH_URL=''; O_TOKEN_URL=''; O_INIT_SCOPE=''; O_EXTRA=()
  case "$O_PROVIDER" in
    microsoft)
      [[ "$O_TENANT" =~ ^[A-Za-z0-9.-]+$ ]] || die 'Invalid oauth2.tenant.'
      O_AUTH_URL="https://login.microsoftonline.com/$O_TENANT/oauth2/v2.0/authorize"
      O_TOKEN_URL="https://login.microsoftonline.com/$O_TENANT/oauth2/v2.0/token"
      O_EXTRA=(response_mode=query)
      if [ -z "$O_SCOPE" ]; then
        if [ "$grant" = client_credentials ]; then
          if [ "$target" = smtp ]; then O_SCOPE='https://outlook.office365.com/.default'; else O_SCOPE='https://graph.microsoft.com/.default'; fi
        elif [ "$target" = smtp ]; then O_SCOPE='https://outlook.office.com/SMTP.Send offline_access'
        else O_SCOPE='https://graph.microsoft.com/Mail.Send offline_access'; fi
      fi
      O_INIT_SCOPE="$O_SCOPE"
      ;;
    google)
      O_AUTH_URL='https://accounts.google.com/o/oauth2/v2/auth'
      O_TOKEN_URL='https://oauth2.googleapis.com/token'
      O_EXTRA=(access_type=offline prompt=consent)
      O_INIT_SCOPE="${O_SCOPE:-https://mail.google.com/}"
      ;;
    generic)
      cfg_str "$prefix.authorize_url" ''; O_AUTH_URL="$REPLY"
      require_string "$prefix.token_url" oauth2 token_url; O_TOKEN_URL="$REPLY"
      O_INIT_SCOPE="$O_SCOPE"
      ;;
    *) die "Unsupported oauth2.provider: $O_PROVIDER" ;;
  esac
  [[ "$O_TOKEN_URL" == https://* ]] || die 'OAuth2 token_url must use HTTPS.'
}

# wait_loopback_redirect PORT: local web server (python3) that answers the browser redirect and
# sets REDIRECTED to the full redirected URL. Returns 1 when it cannot listen.
wait_loopback_redirect() {
  local rc
  command -v python3 > /dev/null 2>&1 || return 1
  cat > "$TMP/listener.py" <<'LISTENER'
import http.server, sys, time, urllib.parse

port, timeout = int(sys.argv[1]), int(sys.argv[2])
result = []


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        done = any(k in urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query) for k in ("code", "error"))
        text = "Authorization received. You can close this tab and return to the terminal." if done else "Waiting for authorization."
        body = ("<!doctype html><meta charset=utf-8><title>mailtest</title><p>%s</p>" % text).encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
        if done:
            result.append(self.path)

    def log_message(self, *args):
        pass


try:
    server = http.server.HTTPServer(("127.0.0.1", port), Handler)
except OSError as error:
    sys.stderr.write(str(error))
    sys.exit(2)
server.timeout = 1
deadline = time.time() + timeout
while not result and time.time() < deadline:
    server.handle_request()
if not result:
    sys.exit(3)
print("http://localhost:%d%s" % (port, result[0]))
LISTENER
  REDIRECTED="$(python3 "$TMP/listener.py" "$1" 300 2> "$TMP/listener.err")"; rc=$?
  [ "$rc" -ne 3 ] || die 'Timed out waiting for the browser redirect.'
  [ "$rc" -eq 0 ]
}

# authorization_code_flow CLIENT_SECRET: authorization code + PKCE (S256) with a loopback redirect.
# Falls back to pasting the redirected URL when the local server cannot start. Sets TOKEN_JSON.
authorization_code_flow() {
  local secret="$1" port redirect state verifier url param pasted
  local -a pairs
  [ -n "$O_AUTH_URL" ] || die 'oauth2.authorize_url is required for --init-oauth with provider: generic.'
  port="$O_PORT"; [ "$port" -gt 0 ] 2> /dev/null || port=$((49152 + RANDOM % 16000))
  redirect="http://localhost:$port"
  state="$(openssl rand -hex 16)"
  verifier="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=\n')"
  url="$O_AUTH_URL?client_id=$(urlencode "$O_CLIENT_ID")&redirect_uri=$(urlencode "$redirect")&response_type=code"
  url+="&scope=$(urlencode "$O_INIT_SCOPE")&state=$state&code_challenge=$(pkce_challenge "$verifier")&code_challenge_method=S256"
  for param in "${O_EXTRA[@]}"; do url+="&$param"; done
  printf 'Open this URL in a browser, sign in and approve access:\n\n%s\n\n' "$url"
  printf 'Waiting for the browser redirect on %s ...\n' "$redirect"
  if ! wait_loopback_redirect "$port"; then
    printf 'Cannot listen on %s (python3 is missing or the port is in use).\n' "$redirect"
    printf 'After approving, the browser shows a connection error for %s. That is expected:\n' "$redirect"
    printf 'copy the full address-bar URL (or just its code= value) and paste it here.\n'
    read -r -p 'Redirected URL or code: ' pasted || die 'No authorization code was provided.'
    REDIRECTED="$pasted"
  fi
  authorization_code "$REDIRECTED" "$state"
  pairs=(grant_type=authorization_code "code=$REPLY" "client_id=$O_CLIENT_ID" "redirect_uri=$redirect" "code_verifier=$verifier")
  [ -z "$secret" ] || pairs+=("client_secret=$secret")
  post_form "$O_TOKEN_URL" "${pairs[@]}" || die "Token exchange failed: $HTTP_BODY"
  [ "$HTTP_CODE" = 200 ] || die "Token exchange failed: $(oauth_error)"
  TOKEN_JSON="$HTTP_BODY"
}

prompt_value() { # PROMPT [secret] -> REPLY (trimmed)
  local value
  if [ "${2:-}" = secret ]; then read -r -s -p "$1: " value; printf '\n'; else read -r -p "$1: " value; fi
  value="${value#"${value%%[![:space:]]*}"}"; REPLY="${value%"${value##*[![:space:]]}"}"
}

read_choice() { # PROMPT COUNT -> CHOICE (1..COUNT); q quits
  local answer
  read -r -p "$1" answer || exit 0
  [ "$answer" != q ] && [ "$answer" != Q ] || exit 0
  if ! [[ "$answer" =~ ^[0-9]+$ ]] || [ "$answer" -lt 1 ] || [ "$answer" -gt "$2" ]; then die "Choose a number from 1 to $2, or q to quit."; fi
  CHOICE="$answer"
}

# Profiles that sign in with OAuth2 and keep a refresh token: the providers --init-oauth can authorize.
list_oauth_providers() {
  local name type oauth where label
  PROVIDER_NAMES=(); PROVIDER_LABELS=()
  while IFS= read -r name; do
    cfg_str "profiles.$name.type" ''; type="$REPLY"
    if [ "$type" = smtp ]; then
      cfg_str "profiles.$name.auth.mode" none; [ "$REPLY" = xoauth2 ] || continue
      oauth="profiles.$name.auth.oauth2"
      cfg_str "profiles.$name.host" ''; where="SMTP $REPLY"
    elif [ "$type" = microsoft_graph ]; then
      oauth="profiles.$name.oauth2"; where='Graph sendMail'
    else
      continue
    fi
    cfg_has_children "$oauth" || continue
    cfg_str "$oauth.grant" refresh_token; [ "$REPLY" = refresh_token ] || continue
    cfg_str "$oauth.provider" microsoft
    case "$REPLY" in microsoft) label=Microsoft ;; google) label=Google ;; *) label='OAuth2 provider' ;; esac
    PROVIDER_NAMES+=("$name"); PROVIDER_LABELS+=("$label - $where")
  done < <(awk -F'\t' '{ n = split($1, a, "."); if (a[1] == "profiles" && n >= 2 && !(a[2] in seen)) { seen[a[2]] = 1; print a[2] } }' "$CFG")
}

select_oauth_provider() { # sets PROFILE_NAME
  local i
  list_oauth_providers
  [ "${#PROVIDER_NAMES[@]}" -gt 0 ] || die 'No OAuth2 profile (smtp.auth.mode: xoauth2 or microsoft_graph with grant: refresh_token) in the YAML.'
  if [ ! -t 0 ]; then die "Choose a profile with --profile: $(IFS=,; printf '%s' "${PROVIDER_NAMES[*]}" | sed 's/,/, /g')"; fi
  printf 'Authorize which provider?\n'
  for i in "${!PROVIDER_NAMES[@]}"; do printf '  %s) %-22s %s\n' "$((i + 1))" "${PROVIDER_NAMES[$i]}" "${PROVIDER_LABELS[$i]}"; done
  printf '  q) Quit\n'
  read_choice 'Select: ' "${#PROVIDER_NAMES[@]}"
  PROFILE_NAME="${PROVIDER_NAMES[$((CHOICE - 1))]}"
}

# oauth_init PREFIX TARGET: interactive sign-in; prints the refresh token. Stores nothing, sends nothing.
oauth_init() {
  local prefix="$1" target="$2" secret="" new_refresh env_name=MAILTEST_REFRESH_TOKEN
  load_oauth_settings "$prefix" "$target"
  case "$O_CLIENT_ID" in 00000000-0000-0000-0000-000000000000|example.apps.googleusercontent.com|your-client-id)
    [ -t 0 ] || die "oauth2.client_id of profile '$PROFILE_NAME' is still the template placeholder. Edit $CONFIG_PATH first."
    printf "Profile '%s' still has the template client_id. Enter the values from your app registration\n" "$PROFILE_NAME"
    printf '(nothing is saved here; put them into %s to reuse them).\n' "$CONFIG_PATH"
    prompt_value client_id; [ -n "$REPLY" ] || die 'client_id is required.'
    O_CLIENT_ID="$REPLY" ;;
  esac
  if [ "$O_PROVIDER" = generic ] && [ -t 0 ] && [[ "$O_AUTH_URL" == *.example.com* ]]; then
    prompt_value authorize_url; O_AUTH_URL="$REPLY"
    prompt_value token_url; O_TOKEN_URL="$REPLY"
    prompt_value "scope [$O_INIT_SCOPE]"; [ -z "$REPLY" ] || O_INIT_SCOPE="$REPLY"
    [[ "$O_AUTH_URL" == https://* && "$O_TOKEN_URL" == https://* ]] || die 'authorize_url and token_url must be HTTPS URLs.'
  fi
  if [ "$O_PROVIDER" = microsoft ] && [ "$O_FLOW" = device_code ]; then
    microsoft_device_code "$O_TENANT" "$O_CLIENT_ID" "$O_INIT_SCOPE"
  else
    if [ "$O_PROVIDER" = google ] || [ -n "${O_CLIENT_SECRET//[[:space:]]/}" ]; then
      local secret_var=""
      [[ "$O_CLIENT_SECRET" =~ ^env:([A-Za-z_][A-Za-z0-9_]*)$ ]] && secret_var="${BASH_REMATCH[1]}"
      if [ -t 0 ] && { [ -z "${O_CLIENT_SECRET//[[:space:]]/}" ] || { [ -n "$secret_var" ] && [ -z "${!secret_var-}" ]; }; }; then
        prompt_value 'client_secret (input hidden, empty for none)' secret; secret="$REPLY"
      else
        resolve_secret "$O_CLIENT_SECRET" oauth2.client_secret; secret="$REPLY"
      fi
    fi
    authorization_code_flow "$secret"
  fi
  new_refresh="$(json_field refresh_token "$TOKEN_JSON")"
  [ -n "$new_refresh" ] || die 'No refresh token returned; ensure offline access was granted.'
  cfg_str "$prefix.refresh_token" ''
  if [[ "$REPLY" =~ ^env:([A-Za-z_][A-Za-z0-9_]*)$ ]]; then env_name="${BASH_REMATCH[1]}"; fi
  printf '\n[OK] Authorization complete.\n'
  warn 'The refresh token below is a long-lived secret. Store it in a secret manager or environment variable; never commit it or share logs.'
  printf 'REFRESH_TOKEN=%s\n\nNext steps:\n' "$new_refresh"
  printf "  1. Keep the token, for example for this shell:  export %s='<token>'\n" "$env_name"
  printf '  2. Test it with this tool:                      ./mailtest.sh --profile %s\n' "$PROFILE_NAME"
  printf '  3. Give it to LMail: OAuth2Token.FromRefreshToken(token) - see docs/MAILTEST.md\n'
}

# get_access_token PREFIX TARGET(smtp|graph): sets ACCESS_TOKEN.
get_access_token() {
  local prefix="$1" target="$2" grant refresh new_refresh
  local -a form
  cfg_str "$prefix.grant" refresh_token; grant="$REPLY"
  if [ "$grant" = access_token ]; then
    cfg_str "$prefix.access_token"; resolve_secret "$REPLY" oauth2.access_token; ACCESS_TOKEN="$REPLY"; return
  fi
  load_oauth_settings "$prefix" "$target"
  if [ "$grant" = client_credentials ]; then
    [ "$O_PROVIDER" = microsoft ] || die 'client_credentials is currently supported only for Microsoft.'
    case "$O_TENANT" in common|organizations|consumers) die 'Microsoft client_credentials requires a specific tenant ID or tenant domain, not common/organizations/consumers.' ;; esac
    resolve_secret "$O_CLIENT_SECRET" oauth2.client_secret
    post_form "$O_TOKEN_URL" grant_type=client_credentials "client_id=$O_CLIENT_ID" "client_secret=$REPLY" "scope=$O_SCOPE" \
      || die "OAuth client_credentials request failed: $HTTP_BODY"
    ACCESS_TOKEN="$(json_field access_token "$HTTP_BODY")"
    [ "$HTTP_CODE" = 200 ] && [ -n "$ACCESS_TOKEN" ] || die "OAuth client_credentials failed: $(oauth_error)"
    return
  fi
  [ "$grant" = refresh_token ] || die "Unsupported oauth2.grant: $grant"
  cfg_str "$prefix.refresh_token" ''
  resolve_secret "$REPLY" oauth2.refresh_token; refresh="$REPLY"
  form=(grant_type=refresh_token "client_id=$O_CLIENT_ID" "refresh_token=$refresh")
  [ -z "$O_SCOPE" ] || form+=("scope=$O_SCOPE")
  if [ -n "${O_CLIENT_SECRET//[[:space:]]/}" ]; then resolve_secret "$O_CLIENT_SECRET" oauth2.client_secret; form+=("client_secret=$REPLY"); fi
  post_form "$O_TOKEN_URL" "${form[@]}" || die "OAuth refresh request failed: $HTTP_BODY"
  [ "$HTTP_CODE" = 200 ] || die "OAuth refresh failed: $(oauth_error). Run --init-oauth to sign in again."
  ACCESS_TOKEN="$(json_field access_token "$HTTP_BODY")"
  [ -n "$ACCESS_TOKEN" ] || die 'OAuth token endpoint returned no access_token.'
  new_refresh="$(json_field refresh_token "$HTTP_BODY")"
  if [ -n "$new_refresh" ] && [ "$new_refresh" != "$refresh" ]; then
    warn 'The provider rotated the refresh token. Update your stored value before the next run.'
  fi
}

# ---------------------------------------------------------------------------------------------
# SMTP through curl (smtp:// with STARTTLS, smtps:// for implicit TLS). Certificates are verified.
# ---------------------------------------------------------------------------------------------
print_trace() { # redacted conversation from a curl -v log
  awk '
    /^> / { line = substr($0, 3); sub(/\r$/, "", line)
            if (line ~ /^AUTH / || line ~ /^[A-Za-z0-9+\/=]{8,}$/ || line == "") print "[SMTP] C: [AUTH data hidden]"; else print "[SMTP] C: " line; next }
    /^< / { line = substr($0, 3); sub(/\r$/, "", line)
            if (line ~ /^334 /) print "[SMTP] S: 334 (authentication challenge redacted)"; else print "[SMTP] S: " line }
  ' "$1"
}

smtp_auth_mechanisms() { # curl -v log -> mechanisms from the last EHLO reply, upper case
  awk '
    /^> EHLO/ { list = "" }
    /^< 250[- ]AUTH[ =]/ { line = $0; sub(/\r$/, "", line); sub(/^< 250[- ]AUTH[ =]/, "", line); list = list " " toupper(line) }
    END { print list }
  ' "$1"
}

last_smtp_reply() { # curl -v log -> last server reply with a 4xx/5xx code
  awk '/^< [45][0-9][0-9][ -]/ { sub(/^< /, ""); sub(/\r$/, ""); last = $0 } END { print last }' "$1"
}

send_smtp() {
  local prefix="$1" host port security timeout mode user scheme url mime rcpt trace
  local -a args=() seen_rcpt=() recipients=() mechs
  require_string "$prefix.host" smtp host; host="$REPLY"
  cfg_str "$prefix.port" 587; port="$REPLY"
  cfg_str "$prefix.security" starttls; security="$REPLY"
  cfg_str "$prefix.timeout_seconds" 25; timeout="$REPLY"
  cfg_str "$prefix.auth.mode" none; mode="$REPLY"
  [ "$security" != none ] || warn 'SMTP connection is not encrypted. This is appropriate only for a trusted relay/test environment.'

  ACCESS_TOKEN=""
  if [ "$mode" = xoauth2 ]; then
    cfg_exists "$prefix.auth.oauth2" || die 'smtp.auth.oauth2 is required for xoauth2.'
    get_access_token "$prefix.auth.oauth2" smtp
  fi

  if [ "$security" = ssl ]; then scheme=smtps; else scheme=smtp; fi
  # The URL path is the EHLO domain; without it curl would announce the upload file name.
  url="$scheme://$host:$port/localhost"
  args=(--silent --show-error --verbose --connect-timeout "$timeout" --max-time $((timeout * 4)))
  [ "$security" != starttls ] || args+=(--ssl-reqd)

  trace="$TMP/smtp.trace"
  HTTP_CFG=""
  if [ "$mode" != none ]; then
    # Probe the advertised mechanisms first: curl would otherwise skip authentication silently
    # when its preferred mechanism is missing, or pick CRAM-MD5/NTLM/SCRAM on its own.
    curl "${args[@]}" --request NOOP "$url" > /dev/null 2> "$trace" || {
      die "SMTP connection failed: $(last_smtp_reply "$trace")$(grep -E '^curl:' "$trace" | head -n 1)"
    }
    # shellcheck disable=SC2207
    mechs=($(smtp_auth_mechanisms "$trace"))
    require_string "$prefix.auth.username" smtp.auth username; user="$REPLY"
    if [ "$mode" = auto ]; then
      if printf ' %s ' "${mechs[*]-}" | grep -q ' PLAIN '; then mode=plain
      elif printf ' %s ' "${mechs[*]-}" | grep -q ' LOGIN '; then mode=login
      else die 'Server did not advertise supported AUTH mechanisms (PLAIN/LOGIN).'; fi
    fi
    printf ' %s ' "${mechs[*]-}" | grep -qi " $mode " || die "SMTP server did not advertise AUTH $(printf '%s' "$mode" | tr '[:lower:]' '[:upper:]')."
    if [ "$mode" = xoauth2 ]; then
      HTTP_CFG+="user = $(curl_quote "$user")"$'\n'"oauth2-bearer = $(curl_quote "$ACCESS_TOKEN")"$'\n'
      HTTP_CFG+='login-options = "AUTH=XOAUTH2"'$'\n'
    else
      cfg_str "$prefix.auth.password"; resolve_secret "$REPLY" smtp.auth.password
      HTTP_CFG+="user = $(curl_quote "$user:$REPLY")"$'\n'
      HTTP_CFG+="login-options = \"AUTH=$(printf '%s' "$mode" | tr '[:lower:]' '[:upper:]')\""$'\n'
    fi
  fi

  mime="$TMP/message.eml"
  build_mime "$mime"
  for rcpt in "${M_TO[@]}" "${M_CC[@]+"${M_CC[@]}"}" "${M_BCC[@]+"${M_BCC[@]}"}"; do
    case " ${seen_rcpt[*]-} " in *" $rcpt "*) continue ;; esac
    seen_rcpt+=("$rcpt"); recipients+=(--mail-rcpt "$rcpt")
  done

  if ! printf '%s\n' "$HTTP_CFG" | curl "${args[@]}" -K - --mail-from "$M_FROM" "${recipients[@]}" -T "$mime" "$url" > /dev/null 2> "$trace"; then
    [ "$TRACE_SMTP" -eq 0 ] || print_trace "$trace"
    local reason
    reason="$(last_smtp_reply "$trace")"
    [ -n "$reason" ] || reason="$(grep -E '^curl:' "$trace" | head -n 1)"
    die "SMTP send failed: ${reason:-unknown error (run with --verbose-protocol)}"
  fi
  [ "$TRACE_SMTP" -eq 0 ] || print_trace "$trace"
  printf '[OK] SMTP accepted the test mail: %s:%s (%s); recipients=%s\n' "$host" "$port" "$security" "${#seen_rcpt[@]}"
  printf '     Server reply: %s\n' "$(awk '/^< 250 .*[Aa]ccept|^< 250 / { sub(/^< /, ""); sub(/\r$/, ""); last = $0 } END { print last }' "$trace")"
}

# ---------------------------------------------------------------------------------------------
# REST providers
# ---------------------------------------------------------------------------------------------
json_address_list() { # addr... -> ["a","b"]
  local out="" a
  for a in "$@"; do out+="${out:+,}$(json_string "$a")"; done
  printf '[%s]' "$out"
}

count_json_array() { # name body -> number of elements of a flat string array
  local inner
  inner="$(printf '%s' "$2" | tr -d '\n' | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\\[\\([^]]*\\)\\].*/\\1/p" | head -n 1)"
  if [ -z "${inner//[[:space:]]/}" ]; then printf 0; else printf '%s' "$inner" | awk -F, '{ print NF }'; fi
}

send_cloudflare() {
  local prefix="$1" account token payload="" uri errors success
  require_string "$prefix.account_id" cloudflare_api account_id; account="$REPLY"
  [[ "$account" =~ ^[a-fA-F0-9]{32}$ ]] || die 'cloudflare_api.account_id must be a 32-character hex ID.'
  cfg_str "$prefix.api_token"; resolve_secret "$REPLY" cloudflare_api.api_token; token="$REPLY"
  payload="{\"from\":$(json_string "$M_FROM"),\"to\":$(json_address_list "${M_TO[@]}"),\"subject\":$(json_string "$M_SUBJECT")"
  [ "${#M_CC[@]}" -eq 0 ] || payload+=",\"cc\":$(json_address_list "${M_CC[@]}")"
  [ "${#M_BCC[@]}" -eq 0 ] || payload+=",\"bcc\":$(json_address_list "${M_BCC[@]}")"
  [ -z "$M_TEXT" ] || payload+=",\"text\":$(json_string "$M_TEXT")"
  [ -z "$M_HTML" ] || payload+=",\"html\":$(json_string "$M_HTML")"
  payload+="}"
  printf '%s' "$payload" > "$TMP/payload.json"
  uri="https://api.cloudflare.com/client/v4/accounts/$account/email/sending/send"
  HTTP_CFG="header = $(curl_quote "Authorization: Bearer $token")"$'\n'
  http_call -X POST -H 'Content-Type: application/json; charset=utf-8' --data-binary "@$TMP/payload.json" "$uri" \
    || die "Cloudflare request failed: $HTTP_BODY"
  if [ "${HTTP_CODE:0:1}" != 2 ]; then die "Cloudflare send failed with HTTP $HTTP_CODE: $(printf '%s' "$HTTP_BODY" | tr '\n' ' ' | cut -c1-300)"; fi
  success="$(printf '%s' "$HTTP_BODY" | tr -d '[:space:]' | grep -o '"success":[a-z]*' | head -n 1)"
  if [ "$success" != '"success":true' ]; then
    errors="$(json_field message "$HTTP_BODY")"
    die "Cloudflare API returned success=false: ${errors:-no details}"
  fi
  local delivered queued suppressed bounced
  delivered="$(count_json_array delivered "$HTTP_BODY")"; queued="$(count_json_array queued "$HTTP_BODY")"
  suppressed="$(count_json_array suppressed_recipients "$HTTP_BODY")"; bounced="$(count_json_array permanent_bounces "$HTTP_BODY")"
  printf '[OK] Cloudflare API accepted the request. message_id=%s; delivered=%s; queued=%s\n' \
    "$(json_field message_id "$HTTP_BODY")" "$delivered" "$queued"
  if [ "$suppressed" -gt 0 ] || [ "$bounced" -gt 0 ]; then
    die "Cloudflare reports suppressed=$suppressed, permanent_bounces=$bounced; not a clean delivery test."
  fi
}

graph_recipients() { # name addr...
  local name="$1" out="" a
  shift
  for a in "$@"; do out+="${out:+,}{\"emailAddress\":{\"address\":$(json_string "$a")}}"; done
  printf '"%s":[%s]' "$name" "$out"
}

send_graph() {
  local prefix="$1" grant mailbox endpoint content_type content body
  cfg_exists "$prefix.oauth2" || die 'microsoft_graph.oauth2 must be specified.'
  cfg_str "$prefix.oauth2.provider" microsoft
  [ "$REPLY" = microsoft ] || die 'microsoft_graph requires oauth2.provider: microsoft.'
  get_access_token "$prefix.oauth2" graph
  cfg_str "$prefix.oauth2.grant" refresh_token; grant="$REPLY"
  if [ "$grant" = client_credentials ]; then
    cfg_str "$prefix.mailbox" "$M_FROM"; mailbox="$REPLY"
    check_mail_address "$mailbox" microsoft_graph.mailbox
    [ "$mailbox" = "$M_FROM" ] || die 'For Graph application auth, microsoft_graph.mailbox must equal message.from.'
    endpoint="https://graph.microsoft.com/v1.0/users/$(urlencode "$mailbox")/sendMail"
  else
    endpoint='https://graph.microsoft.com/v1.0/me/sendMail'
    warn 'Graph delegated mode sends as the signed-in user (not necessarily message.from). Configure SendAs separately if needed.'
  fi
  if [ -n "$M_HTML" ]; then content_type=HTML; content="$M_HTML"; else content_type=Text; content="$M_TEXT"; fi
  body="{\"message\":{\"subject\":$(json_string "$M_SUBJECT"),\"body\":{\"contentType\":\"$content_type\",\"content\":$(json_string "$content")},$(graph_recipients toRecipients "${M_TO[@]}")"
  [ "${#M_CC[@]}" -eq 0 ] || body+=",$(graph_recipients ccRecipients "${M_CC[@]}")"
  [ "${#M_BCC[@]}" -eq 0 ] || body+=",$(graph_recipients bccRecipients "${M_BCC[@]}")"
  body+="},\"saveToSentItems\":false}"
  printf '%s' "$body" > "$TMP/payload.json"
  HTTP_CFG="header = $(curl_quote "Authorization: Bearer $ACCESS_TOKEN")"$'\n'
  http_call -X POST -H 'Content-Type: application/json; charset=utf-8' --data-binary "@$TMP/payload.json" "$endpoint" \
    || die "Graph request failed: $HTTP_BODY"
  [ "$HTTP_CODE" = 202 ] || die "Microsoft Graph sendMail failed with HTTP $HTTP_CODE: $(json_field code "$HTTP_BODY") $(json_field message "$HTTP_BODY")"
  printf '[OK] Microsoft Graph accepted sendMail (HTTP 202). This is not proof of final inbox delivery.\n'
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------
main() {
  local arg item transport profile_prefix oauth_prefix err_file action_args=0
  while [ $# -gt 0 ]; do
    arg="$1"
    action_args=$((action_args + 1))
    case "$arg" in
      --config) [ $# -ge 2 ] || die '--config needs a value.'; CONFIG_PATH="$2"; action_args=$((action_args - 1)); shift; shift; continue ;;
      --profile) [ $# -ge 2 ] || die '--profile needs a value.'; PROFILE_NAME="$2"; shift ;;
      --to) [ $# -ge 2 ] || die '--to needs a value.'; IFS=',' read -r -a items <<< "$2"; for item in "${items[@]}"; do OVERRIDE_TO+=("${item//[[:space:]]/}"); done; shift ;;
      --subject) [ $# -ge 2 ] || die '--subject needs a value.'; OVERRIDE_SUBJECT="$2"; HAS_SUBJECT=1; shift ;;
      --body) [ $# -ge 2 ] || die '--body needs a value.'; OVERRIDE_BODY="$2"; HAS_BODY=1; shift ;;
      --init-oauth) INIT_OAUTH=1 ;;
      --validate-only) VALIDATE_ONLY=1 ;;
      --verbose-protocol) TRACE_SMTP=1 ;;
      -h|--help) usage; exit 0 ;;
      *) usage >&2; die "Unknown option: $arg" ;;
    esac
    shift
  done

  command -v curl > /dev/null || die 'curl is required.'
  command -v openssl > /dev/null || die 'openssl is required.'

  if [ ! -e "$CONFIG_PATH" ]; then
    mkdir -p "$(dirname "$CONFIG_PATH")"
    if ( set -C; write_template > "$CONFIG_PATH" ) 2> /dev/null; then
      printf '[SETUP] Created %s from the built-in template (edit it to add your profiles).\n' "$CONFIG_PATH"
    fi
  fi
  [ -f "$CONFIG_PATH" ] || die "Cannot read configuration: $CONFIG_PATH"

  TMP="$(mktemp -d)"; chmod 700 "$TMP"
  trap 'rm -rf "$TMP"' EXIT
  CFG="$TMP/config.flat"; err_file="$TMP/yaml.err"
  if ! yaml_flatten "$CONFIG_PATH" > "$CFG" 2> "$err_file"; then
    die "$(sed 's/^YAML: //' "$err_file" | head -n 1)"
  fi

  cfg_has_children profiles || die 'YAML must contain profiles: mapping.'
  if [ "$action_args" -eq 0 ] && [ -t 0 ]; then
    require_string default_profile root default_profile
    printf 'mailtest - choose an action:\n  1) Send a test mail (profile: %s)\n  2) Authorize an OAuth2 provider and get a refresh token\n  q) Quit\n' "$REPLY"
    read_choice 'Select: ' 2
    [ "$CHOICE" -ne 2 ] || INIT_OAUTH=1
  fi
  if [ "$INIT_OAUTH" -eq 1 ] && [ -z "$PROFILE_NAME" ] && [ "$VALIDATE_ONLY" -eq 0 ]; then select_oauth_provider; fi
  if [ -z "$PROFILE_NAME" ]; then require_string default_profile root default_profile; PROFILE_NAME="$REPLY"; fi
  profile_prefix="profiles.$PROFILE_NAME"
  cfg_has_children "$profile_prefix" || die "Profile '$PROFILE_NAME' does not exist in YAML profiles."
  require_string "$profile_prefix.type" "profiles.$PROFILE_NAME" type; transport="$REPLY"
  cfg_has_children message || die 'YAML must contain a message: mapping.'

  if [ "$INIT_OAUTH" -eq 1 ]; then
    [ "$VALIDATE_ONLY" -eq 0 ] || die 'Choose --validate-only or --init-oauth, not both.'
    [ "$transport" != cloudflare_api ] || die '--init-oauth is not supported for Cloudflare.'
    if [ "$transport" = smtp ]; then
      cfg_str "$profile_prefix.auth.mode" none
      [ "$REPLY" = xoauth2 ] || die '--init-oauth requires smtp.auth.mode: xoauth2.'
      oauth_prefix="$profile_prefix.auth.oauth2"
    else
      oauth_prefix="$profile_prefix.oauth2"
    fi
    M_FROM=''; M_TO=(); M_CC=(); M_BCC=()
    test_profile_settings "$profile_prefix" "$transport"
    cfg_str "$oauth_prefix.grant" refresh_token
    [ "$REPLY" = refresh_token ] || die '--init-oauth requires oauth2.grant: refresh_token.'
    oauth_init "$oauth_prefix" "$([ "$transport" = smtp ] && echo smtp || echo graph)"
    exit 0
  fi

  load_message
  if [ "$HAS_SUBJECT" -eq 1 ]; then
    [[ "$OVERRIDE_SUBJECT" != *$'\n'* && "$OVERRIDE_SUBJECT" != *$'\r'* ]] || die 'Subject must not contain CR/LF.'
    assert_subject "$OVERRIDE_SUBJECT"
    M_SUBJECT="$OVERRIDE_SUBJECT"
  fi
  if [ "$HAS_BODY" -eq 1 ]; then M_TEXT="$OVERRIDE_BODY"; M_HTML=''; fi
  if [ -z "$M_TEXT" ] && [ -z "$M_HTML" ]; then die 'At least one of message.text or message.html must be present after overrides.'; fi

  test_profile_settings "$profile_prefix" "$transport"

  if [ "$VALIDATE_ONLY" -eq 1 ]; then
    printf "[OK] YAML loaded; profile='%s'; type='%s'; from='%s'; to=%s.\n" "$PROFILE_NAME" "$transport" "$M_FROM" "${#M_TO[@]}"
    printf '     Checked YAML, chosen profile and message fields. No secrets resolved; no network connection or authentication attempted.\n'
    exit 0
  fi

  if [ "$M_FROM" = sender@example.com ] && [[ " ${M_TO[*]} " == *" recipient@example.net "* ]]; then
    die "message.from and message.to still hold the template placeholders. Edit $CONFIG_PATH first."
  fi
  printf "Testing profile '%s' (%s) -> %s\n" "$PROFILE_NAME" "$transport" "$(IFS=,; printf '%s' "${M_TO[*]}" | sed 's/,/, /g')"
  case "$transport" in
    smtp) send_smtp "$profile_prefix" ;;
    cloudflare_api) send_cloudflare "$profile_prefix" ;;
    microsoft_graph) send_graph "$profile_prefix" ;;
  esac
  exit 0
}

main "$@"
