using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AzureDevOpsForager.Core.Services.Utilities
{
   /// <summary>
   /// Strips the boilerplate context header from a chunk before it is handed to an embedding model
   /// or a cross-encoder reranker. <b>NOT CURRENTLY WIRED IN - the hypothesis was tested and
   /// falsified.</b> Kept because the measurement and the negative result are worth preserving.
   /// </summary>
   /// <remarks>
   /// The chunker prepends a context header to every member chunk (file path, namespace, enclosing
   /// class, and up to five field declarations) so that a search hit on a bare method still tells a
   /// human reader where it lives. That is a display concern, but the same text was being embedded.
   ///
   /// Measured on the eShopOnWeb corpus, 2026-08-28: the header accounted for <b>33.3%</b> of all
   /// characters embedded across 605 chunks, <b>27%</b> of chunks were more than half header, and
   /// <b>36%</b> carried the header twice because the sibling-overlap feature appends the next
   /// chunk's raw content, header included. Since the header is identical for every chunk in a
   /// file, those chunks embed to nearly the same vector, and that vector describes the file's
   /// header rather than what the code does. Two different embedders and two different rerankers
   /// failed on exactly the same six questions, which is the signature of a shared input defect
   /// rather than a model weakness.
   ///
   /// The stored <c>ChunkContent</c> is deliberately left alone: it is what the UI displays and
   /// what the full-text index searches. Only the text handed to the models is normalized, so this
   /// changes retrieval quality without changing what a user sees or what FTS can match.
   ///
   /// <para><b>Result, measured the same day.</b> Built, full corpus reindexed, identical battery
   /// rerun. Semantic retrieval got <i>worse</i>: rank-1 3 to 3, top-5 6 to 4, MRR 0.304 to 0.238,
   /// and not one of the six questions that both stacks missed moved. Exact-identifier queries did
   /// improve, 3/4 to 4/4 rank-1, from less text diluting a literal name match.</para>
   ///
   /// <para><b>Why it hurt.</b> The header was not pure noise. <c>// Class: RegisterModel</c> and
   /// the file path were the only occurrences of "register" in that chunk, so Register.cshtml.cs
   /// fell from rank 1 to absent on "where does someone create a new login";
   /// IdentityTokenClaimService.cs fell from rank 2 to absent the same way. Class and file names
   /// are genuine semantic signal for natural-language questions. The actually-redundant parts are
   /// the <c>// Fields:</c> list and the header repeated inside the sibling-overlap block.</para>
   ///
   /// <para>If revisited: keep the class and file names, drop only the field list and the duplicated
   /// overlap header. That is a different hypothesis and needs its own before/after measurement.</para>
   /// </remarks>
   public static class ChunkTextNormalizer
   {
      #region Fields

      /// <summary>
      /// Context-header lines emitted by the chunker's BuildContextPrefix. These repeat verbatim on
      /// every chunk of a file, so they carry no signal that distinguishes one chunk from another.
      /// </summary>
      private static readonly Regex _headerLine = new Regex(
         @"^\s*//\s*(File|Namespace|Class|Fields)\s*:",
         RegexOptions.Compiled | RegexOptions.IgnoreCase );

      /// <summary>
      /// The separator the chunker inserts before appended sibling-overlap text. The overlap body is
      /// kept because it is real code; only the marker is dropped.
      /// </summary>
      private static readonly Regex _overlapMarker = new Regex(
         @"^\s*//\s*-{2,}\s*next-chunk overlap.*$",
         RegexOptions.Compiled | RegexOptions.IgnoreCase );

      /// <summary>Collapses three or more consecutive newlines left behind by removed lines.</summary>
      private static readonly Regex _blankRun = new Regex( @"(\r?\n){3,}", RegexOptions.Compiled );

      #endregion

      #region Public Methods

      /// <summary>
      /// Returns the text that should be sent to an embedding model or a reranker for
      /// <paramref name="chunkContent"/>, with the repeated context header removed.
      /// </summary>
      /// <param name="chunkContent">The stored chunk text, header included.</param>
      /// <returns>
      /// The chunk with header lines and overlap markers removed. Falls back to the original text
      /// when stripping would leave nothing, so a chunk that is <i>only</i> a header still produces
      /// a vector instead of failing the embed call.
      /// </returns>
      public static string ForModel( string chunkContent )
      {
         if( string.IsNullOrWhiteSpace( chunkContent ) )
            return chunkContent;

         var builder = new StringBuilder( chunkContent.Length );

         foreach( var line in chunkContent.Split( '\n' ) )
         {
            if( _headerLine.IsMatch( line ) || _overlapMarker.IsMatch( line ) )
               continue;

            builder.Append( line ).Append( '\n' );
         }

         var stripped = _blankRun.Replace( builder.ToString(), "\n\n" ).Trim();

         // A chunk that was nothing but header has no code to embed. Keeping the original is
         // strictly better than embedding an empty string, which most endpoints reject outright.
         return stripped.Length == 0 ? chunkContent : stripped;
      }

      #endregion
   }
}
