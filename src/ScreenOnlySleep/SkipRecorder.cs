using System.Collections.Generic;
using System.Text;

namespace ScreenOnlySleep
{
  // Counts ignored input per signature with hard caps, so a flood of synthesized input cannot grow the log without bound.
  internal sealed class SkipRecorder
  {
    public const int DefaultMaxDetailPerSignature = 3;
    public const int DefaultMaxDetailTotal = 40;
    public const int DefaultMaxSignatures = 30;
    public const string OtherSignature = "(other signatures)";

    readonly object gate = new object();
    readonly Dictionary<string, int> counts = new Dictionary<string, int>();
    readonly List<string> order = new List<string>();
    readonly int maxPerSig, maxTotal, maxSigs;
    int total, detailCount, detailDropped;

    public SkipRecorder() : this(DefaultMaxDetailPerSignature, DefaultMaxDetailTotal, DefaultMaxSignatures) { }

    public SkipRecorder(int maxDetailPerSignature, int maxDetailTotal, int maxSignatures)
    {
      maxPerSig = maxDetailPerSignature;
      maxTotal = maxDetailTotal;
      maxSigs = maxSignatures;
    }

    public int Total { get { lock (gate) return total; } }
    public int DetailCount { get { lock (gate) return detailCount; } }
    public int SignatureCount { get { lock (gate) return order.Count; } }

    // Counts one event. Returns true if its details should be written to the log.
    public bool Record(string signature)
    {
      lock (gate)
      {
        int c;
        if (!counts.ContainsKey(signature) && order.Count >= maxSigs) signature = OtherSignature;
        if (!counts.TryGetValue(signature, out c)) order.Add(signature);
        counts[signature] = ++c;
        total++;
        bool want = c <= maxPerSig && detailCount < maxTotal;
        if (want) detailCount++; else detailDropped++;
        return want;
      }
    }

    public string Summary()
    {
      lock (gate)
      {
        if (total == 0) return "ignored input: 0";
        var sb = new StringBuilder();
        sb.Append("ignored input: " + total + " (details " + detailCount + ", omitted " + detailDropped + ")");
        foreach (var s in order) sb.Append(" | " + s + " x" + counts[s]);
        return sb.ToString();
      }
    }
  }
}
