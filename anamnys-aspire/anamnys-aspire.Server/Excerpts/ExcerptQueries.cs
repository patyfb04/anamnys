using System.Linq.Expressions;

namespace Anamnys.Server.Excerpts;

// The hard rule of F-03: analysis queries only return items that point back to the text that produced
// them. Every query over ThemeTags, PatientQuotes or ExtractedFacts in Anamnys.Server.Analyses must go
// through this helper (enforced by ExcerptRequireArchitectureTests).
public static class ExcerptQueries
{
    public const int MaxQuoteLength = 200;

    public static IQueryable<T> RequireExcerpt<T>(this IQueryable<T> source, Expression<Func<T, Guid?>> excerptRefId)
    {
        var notNull = Expression.NotEqual(excerptRefId.Body, Expression.Constant(null, typeof(Guid?)));
        return source.Where(Expression.Lambda<Func<T, bool>>(notNull, excerptRefId.Parameters));
    }
}
