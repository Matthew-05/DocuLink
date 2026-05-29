namespace DocuLink.Addin.Modules.UI
{
    internal interface IProgressReporter
    {
        void Report(string message, string detail = null, int current = 0, int total = 0);
    }
}
