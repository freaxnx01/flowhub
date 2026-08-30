using System.Globalization;

namespace FlowHub.Persistence.Repositories;

internal static class RepoEmbeddingSql
{
    // float[] values are IEEE 754 floats — no SQL injection risk in the literal.
    // Mirrors EfCaptureRepository.SearchByEmbeddingAsync.
    public static string ToVectorLiteral(float[] embedding) =>
        "[" + string.Join(",",
            embedding.Select(f => f.ToString("G", CultureInfo.InvariantCulture))) + "]";

    public static HashSet<string> ToOrdinalSet(IEnumerable<string> values) =>
        values.ToHashSet(StringComparer.Ordinal);
}
