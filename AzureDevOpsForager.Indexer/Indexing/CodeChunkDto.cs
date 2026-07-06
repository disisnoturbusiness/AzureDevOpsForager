#nullable enable
namespace AzureDevOpsForager.Indexer.Indexing
{
   /// <summary>
   /// A single chunk of code extracted from a source file, ready to be embedded and
   /// stored in the vector index. Roslyn walks each source file and emits one of these
   /// per logical unit (the whole file, a class, an interface, a method, and so on), so
   /// that code search can match at a finer granularity than "whole file". The namespace,
   /// class, and parent-context fields carry the surrounding Roslyn context, which gives
   /// the embedding model enough scope to disambiguate same-named members across types.
   /// </summary>
   public class CodeChunkDto
   {
      #region Data Members

      /// <summary>
      /// Absolute or repo-relative path of the source file this chunk came from. Forms the
      /// first segment of the stable chunk id, so it must stay consistent across re-indexes
      /// for delta detection to work.
      /// </summary>
      public string FilePath { get; set; } = string.Empty;

      /// <summary>
      /// The kind of code unit this chunk represents: "file", "class", "interface",
      /// "method", "constructor", "property", or "members". Used to weight and filter
      /// search results (a method-level hit is usually more precise than a whole-file hit).
      /// </summary>
      public string ChunkType { get; set; } = string.Empty;

      /// <summary>
      /// The name of the code unit (e.g. the method or class name). Combined with the type
      /// and start line to build a chunk id that survives edits elsewhere in the file.
      /// </summary>
      public string ChunkName { get; set; } = string.Empty;

      /// <summary>
      /// The raw source text of the chunk. This is the text that actually gets embedded and
      /// is what a code search ultimately matches against.
      /// </summary>
      public string Content { get; set; } = string.Empty;

      /// <summary>
      /// One-based line where the chunk begins in the source file. Also participates in the
      /// chunk id so that two same-named members in one file stay distinct.
      /// </summary>
      public int StartLine { get; set; }

      /// <summary>
      /// One-based line where the chunk ends in the source file. Together with the start
      /// line this lets search results jump straight to the exact span in the editor.
      /// </summary>
      public int EndLine { get; set; }

      /// <summary>
      /// The declaration signature (e.g. a method's return type and parameter list) when
      /// Roslyn could extract one. Null for chunk kinds that have no meaningful signature.
      /// </summary>
      public string? Signature { get; set; }

      /// <summary>
      /// The enclosing namespace, when known. Part of the surrounding context that helps the
      /// embedding model tell apart identically named members from different namespaces.
      /// </summary>
      public string? Namespace { get; set; }

      /// <summary>
      /// The enclosing class name, when the chunk lives inside a type. Null for file-level or
      /// top-level chunks that have no containing class.
      /// </summary>
      public string? ClassName { get; set; }

      /// <summary>
      /// A human-readable description of the surrounding scope (namespace / class / field
      /// prefixes) supplied by the Roslyn chunker. Gives the embedding extra locality so that
      /// a bare member name is searchable in context.
      /// </summary>
      public string? ParentContext { get; set; }

      #endregion

      #region Public Methods

      /// <summary>
      /// Builds the stable identity key for this chunk in the form
      /// path:type:name:startline. The index uses it for delta re-indexing (re-embed only
      /// what changed) and for deduplication, so every component that varies between two
      /// distinct chunks in the same file (name and start line) is baked into the key.
      /// </summary>
      public string GetId() => $"{FilePath}:{ChunkType}:{ChunkName}:{StartLine}";

      #endregion
   }
}
