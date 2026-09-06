using System;
using System.Diagnostics;
using System.IO;

namespace Talliark.Addin.Modules.Services
{
    /// <summary>
    /// Starts the downloaded MSI through Windows Installer and writes a verbose
    /// diagnostic log. Windows Installer owns upgrade and files-in-use handling.
    /// </summary>
    internal static class UpdateInstallerService
    {
        internal static void Start(string msiPath)
        {
            if (string.IsNullOrWhiteSpace(msiPath) || !File.Exists(msiPath))
                throw new FileNotFoundException(
                    "The downloaded Talliark installer was not found.", msiPath);

            string logPath = Path.Combine(Path.GetTempPath(), "Talliark-update.log");
            string installerArguments = $"/i \"{msiPath}\" /L*v \"{logPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = installerArguments,
                UseShellExecute = false,
            };

            Process process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Could not start the Talliark installer.");

            process.Dispose();
        }
    }
}
