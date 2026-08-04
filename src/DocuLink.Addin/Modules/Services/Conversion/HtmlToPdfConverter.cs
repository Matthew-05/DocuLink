using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DocuLink.Addin.Modules.Services.Conversion
{
    /// <summary>
    /// Renders HTML to PDF using an offscreen WebView2.
    ///
    /// This is the only HTML rendering engine DocuLink ships, and it is already a
    /// hard dependency of the task pane and viewer, so HTML-shaped sources (.html,
    /// .eml normalised by the Python worker, .msg exported by Outlook) all funnel
    /// through here rather than adding a second renderer.
    ///
    /// The WebView2 control must be created and driven on the UI thread; callers
    /// on a background thread should marshal via the control passed to
    /// <see cref="DocumentConversionService"/>.
    /// </summary>
    internal static class HtmlToPdfConverter
    {
        private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Loads <paramref name="htmlPath"/> in an offscreen WebView2 and prints it
        /// to <paramref name="outputPdfPath"/>. Must be awaited on the UI thread.
        /// </summary>
        public static async Task ConvertFileAsync(string htmlPath, string outputPdfPath)
        {
            if (string.IsNullOrWhiteSpace(htmlPath))
                throw new ArgumentException("An HTML source path is required.", nameof(htmlPath));
            if (!File.Exists(htmlPath))
                throw new FileNotFoundException("HTML source not found.", htmlPath);

            await RenderAsync(new Uri(htmlPath).AbsoluteUri, outputPdfPath);
        }

        private static async Task RenderAsync(string navigateUri, string outputPdfPath)
        {
            // The host form is never shown. WebView2 still needs a real parent
            // handle to initialise, so the form is created off-screen and disposed
            // as soon as printing finishes.
            using (var host = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-10000, -10000),
                Size = new System.Drawing.Size(1024, 1320),
                Opacity = 0,
            })
            using (var webView = new WebView2 { Dock = DockStyle.Fill })
            {
                host.Controls.Add(webView);
                host.Show();          // Realises the handle; the form stays off-screen.

                try
                {
                    string userDataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DocuLink", "WebView2");

                    CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                        browserExecutableFolder: null,
                        userDataFolder: userDataFolder);

                    await webView.EnsureCoreWebView2Async(environment);

                    webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                    await NavigateAsync(webView, navigateUri);

                    // Give late-arriving layout (web fonts, inline images) a moment
                    // to settle before capturing; NavigationCompleted fires before
                    // subresources have necessarily been laid out.
                    await Task.Delay(250);

                    bool printed = await webView.CoreWebView2.PrintToPdfAsync(outputPdfPath);
                    if (!printed)
                        throw new IOException("WebView2 could not print the document to PDF.");
                }
                finally
                {
                    host.Hide();
                }
            }
        }

        private static Task NavigateAsync(WebView2 webView, string uri)
        {
            var completion = new TaskCompletionSource<bool>();
            var timeout = new Timer { Interval = (int)NavigationTimeout.TotalMilliseconds };

            void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                Cleanup();

                if (args.IsSuccess)
                    completion.TrySetResult(true);
                else
                    completion.TrySetException(new IOException(
                        $"Could not load the document for rendering ({args.WebErrorStatus})."));
            }

            void OnTimeout(object sender, EventArgs args)
            {
                Cleanup();
                completion.TrySetException(new TimeoutException(
                    "Timed out loading the document for rendering."));
            }

            void Cleanup()
            {
                timeout.Stop();
                timeout.Tick -= OnTimeout;
                timeout.Dispose();
                webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }

            webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            timeout.Tick += OnTimeout;
            timeout.Start();

            try
            {
                webView.CoreWebView2.Navigate(uri);
            }
            catch (Exception ex)
            {
                Cleanup();
                completion.TrySetException(ex);
            }

            return completion.Task;
        }
    }
}
