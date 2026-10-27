using System;
using System.Threading.Tasks;

namespace LMail.Samples
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || (args.Length == 1 && args[0] == "--self-test"))
            {
                return await SelfTest.RunAsync().ConfigureAwait(false);
            }

            if (args.Length == 2 && args[0] == "--live")
            {
                try
                {
                    return await LiveSend.RunAsync(args[1]).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Live send failed: " + ex.Message);

                    if (ex is LMailException)
                    {
                        Console.Error.WriteLine(ex.GetType().Name);
                    }

                    return 1;
                }
            }

            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  dotnet run -- --self-test");
            Console.Error.WriteLine("  dotnet run -- --live <smtp|gmail|m365|graph|cloudflare|brevo|mailtrap|resend|maileroo|ses>");
            return 1;
        }
    }
}
