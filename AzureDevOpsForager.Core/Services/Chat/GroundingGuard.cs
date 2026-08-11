namespace AzureDevOpsForager.Core.Services.Chat;

/// <summary>
/// Decides whether a question has enough retrieved code behind it to be worth asking the model at all.
/// <para>
/// An LLM handed an empty context does not say "I don't know" — it answers from its training weights and
/// the result is indistinguishable, to the reader, from an answer grounded in the indexed repository.
/// Asked "how do I deploy this to Kubernetes?" against a codebase with no Kubernetes in it, the model
/// returned a confident three-thousand-character tutorial on Dockerfiles and kubectl. Nothing about the
/// response says it came from the model's own knowledge rather than from this repository, which makes it
/// worse than an error: it is a plausible answer attributed to a source that never said it.
/// </para>
/// <para>
/// So grounding is checked before the call, not repaired after it. The retrieval side already refuses to
/// return results it cannot justify (see the relevance gate in HybridSearchService), and this is the same
/// judgement applied one layer up: no sources, no model call. It also means an unanswerable question
/// costs nothing instead of a full completion.
/// </para>
/// <para>
/// This deliberately does not try to detect a hallucination in a grounded answer — that is a much harder
/// problem and not what this guards. It only covers the case where there is provably nothing to ground on.
/// </para>
/// </summary>
public static class GroundingGuard
{
   /// <summary>
   /// The answer returned when retrieval produced nothing. Phrased to say what was actually established —
   /// that the indexed code contains no match — rather than implying the question itself is unanswerable.
   /// </summary>
   public const string NoGroundingAnswer =
      "I could not find anything in the indexed codebase that answers that. " +
      "This assistant only answers from code that was actually retrieved for your question, so rather " +
      "than answer from general knowledge it is telling you the index came back empty. " +
      "Try naming a specific type, method, or file, or widening the question.";

   /// <summary>
   /// True when there is retrieved context worth sending to the model. Whitespace-only context counts as
   /// empty: the context is assembled by concatenating retrieved chunks, so a blank string means every
   /// candidate was filtered out, not that a real chunk happened to be empty.
   /// </summary>
   /// <param name="context">The assembled code context that would be sent to the model.</param>
   public static bool HasGrounding( string context )
   {
      return !string.IsNullOrWhiteSpace( context );
   }
}
