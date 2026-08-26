using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Hands installation to a detached process which waits for Excel to exit.
    /// This keeps loaded VSTO assemblies out of Windows Installer's files-in-use
    /// handling and also writes a verbose diagnostic log for every update.
    /// </summary>
    internal static class UpdateInstallerService
    {
        internal static string ScheduleAfterExcelExit(string msiPath)
        {
            if (string.IsNullOrWhiteSpace(msiPath) || !File.Exists(msiPath))
                throw new FileNotFoundException(
                    "The downloaded DocuLink installer was not found.", msiPath);

            string logPath = Path.Combine(Path.GetTempPath(), "DocuLink-update.log");
            string installerArguments = $"/i \"{msiPath}\" /L*v \"{logPath}\"";

            string command =
                "$excel = Get-Process -Name 'EXCEL' -ErrorAction SilentlyContinue; " +
                "if ($excel) { $excel | Wait-Process -ErrorAction SilentlyContinue }; " +
                $"Start-Process -FilePath 'msiexec.exe' -ArgumentList {QuotePowerShell(installerArguments)}";
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden " +
                    "-EncodedCommand " + encodedCommand,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Could not start the DocuLink update helper.");

            process.Dispose();
            return logPath;
        }

        private static string QuotePowerShell(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }
    }
}
