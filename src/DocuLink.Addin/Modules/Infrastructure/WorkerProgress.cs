using System.Collections.Generic;

namespace DocuLink.Addin.Modules.Infrastructure
{
    /// <summary>
    /// One progress line from the Python worker, as <c>OcrProgressMessage</c> in
    /// contracts/python-worker-v1.json defines it.
    ///
    /// <see cref="Stage"/> is stated by the worker and never inferred from
    /// <see cref="Message"/>. The message is prose for a human and may be reworded
    /// at any time; the stage is the machine-readable fact the file manager
    /// renders. Reading the stage back out of the message is what this type exists
    /// to end — a message the host had no rule for used to be reported as the run
    /// beginning, however far through it actually was.
    ///
    /// <see cref="Current"/> and <see cref="Total"/> count within <see cref="Stage"/>
    /// only. They are not progress through the file, and they reset whenever the
    /// stage changes.
    /// </summary>
    internal sealed class WorkerProgress
    {
        public string Message { get; set; }
        public string Stage { get; set; }
        public int? Current { get; set; }
        public int? Total { get; set; }
        public string Unit { get; set; }

        /// <summary>Reads one deserialized worker progress line, or null when it carries no stage.</summary>
        public static WorkerProgress FromJson(Dictionary<string, object> obj)
        {
            if (obj == null) return null;

            string stage = PythonWorkerSession.GetString(obj, "stage");
            if (string.IsNullOrEmpty(stage)) return null;

            var progress = new WorkerProgress
            {
                Message = PythonWorkerSession.GetString(obj, "message") ?? string.Empty,
                Stage = stage,
                Unit = PythonWorkerSession.GetString(obj, "unit"),
            };

            // The contract pins current and total together; a lone one is ignored
            // rather than rendered as a bar with an unknown denominator.
            if (TryGetInt(obj, "current", out int current)
                && TryGetInt(obj, "total", out int total)
                && total > 0)
            {
                progress.Current = current < 0 ? 0 : (current > total ? total : current);
                progress.Total = total;
            }

            return progress;
        }

        private static bool TryGetInt(Dictionary<string, object> obj, string key, out int value)
        {
            value = 0;
            if (obj == null || !obj.TryGetValue(key, out object raw) || raw == null) return false;
            try
            {
                value = System.Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
