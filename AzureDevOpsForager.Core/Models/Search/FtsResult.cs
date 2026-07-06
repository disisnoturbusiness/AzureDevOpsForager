using System.Collections.Generic;

namespace AzureDevOpsForager.Core.Models.Search;

/// <summary>
/// Represents a single hit from the SQL Server full-text (FTS) search path over indexed
/// source files. Carries the file's metadata alongside the BM25 relevance score that the
/// full-text engine assigns, so callers can rank keyword matches against one another and
/// blend them with the vector-search path when producing a combined result set.
/// </summary>
public class FtsResult
{
   #region Data Members

   /// <summary>
   /// Path of the matched source file within the indexed repository. Doubles as the natural
   /// key for de-duplicating a file that surfaces from both the full-text and vector paths.
   /// </summary>
   public string FilePath { get; set; }

   /// <summary>
   /// The indexed textual content of the file that the full-text match was scored against.
   /// Held here so the caller can render a snippet without re-reading the file from disk.
   /// </summary>
   public string Content { get; set; }

   /// <summary>
   /// Name of the primary type declared in the file (class/struct/interface), extracted at
   /// index time. Surfaced as search metadata so users can spot type-level matches quickly.
   /// </summary>
   public string ClassName { get; set; }

   /// <summary>
   /// Name of the base type the primary type derives from, when one was detected. Useful for
   /// narrowing searches to a particular inheritance family.
   /// </summary>
   public string BaseClass { get; set; }

   /// <summary>
   /// Delimited list of method names discovered in the file. Kept as a single string because
   /// that is the shape the index stores and the API contract returns.
   /// </summary>
   public string MethodNames { get; set; }

   /// <summary>
   /// Delimited list of property names discovered in the file, mirroring <see cref="MethodNames"/>
   /// in storage shape.
   /// </summary>
   public string PropertyNames { get; set; }

   /// <summary>
   /// Delimited list of enum member values found in the file. Empty for files that declare no
   /// enums; retained so enum-symbol searches can match against it.
   /// </summary>
   public string EnumValues { get; set; }

   /// <summary>
   /// Category of the file (for example the language or role), used to filter or group results.
   /// </summary>
   public string FileType { get; set; }

   /// <summary>
   /// The BM25 relevance score returned by the full-text engine. Higher means a stronger
   /// keyword match; used to order full-text hits and to weigh them during result blending.
   /// </summary>
   public double Rank { get; set; }

   #endregion

   #region Public Methods

   /// <summary>
   /// Projects the strongly-typed metadata fields into the flat, string-keyed dictionary shape
   /// that the search API returns to clients. The snake_case keys are the external contract, so
   /// they are kept verbatim regardless of the C# property names they map from. Note that
   /// <see cref="FilePath"/> and <see cref="Rank"/> are intentionally excluded here: they are
   /// carried separately on the response envelope rather than inside the metadata payload.
   /// </summary>
   /// <returns>A metadata dictionary keyed by the API's public field names.</returns>
   public Dictionary<string, string> ToMetadata()
   {
      return new Dictionary<string, string>
      {
         { "class_name", ClassName },
         { "base_class", BaseClass },
         { "methods", MethodNames },
         { "properties", PropertyNames },
         { "enum_values", EnumValues },
         { "file_type", FileType }
      };
   }

   #endregion
}
