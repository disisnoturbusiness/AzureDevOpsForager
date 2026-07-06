#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AzureDevOpsForager.Indexer.Indexing;

/// <summary>
/// Roslyn-based code chunker. Splits a C# source file into semantically meaningful chunks
/// (class shells, interfaces, individual members) that feed the code-search embedding pipeline.
/// Each chunk carries enough surrounding context (namespace, enclosing class, sibling field
/// declarations) that a semantic search hit still makes sense in isolation, and every chunk is
/// kept within a token budget so it survives the embedding model's truncation cap.
/// </summary>
public class RoslynChunker
{
   #region Data Members

   /// <summary>
   /// Below this estimated-token count a member chunk is considered "undersized" and becomes a
   /// candidate for merging with adjacent small members, so we don't waste an embedding on a
   /// two-line property.
   /// </summary>
   private const int MinTokenEstimate = 50;

   /// <summary>
   /// Lower edge of the preferred chunk size band. Retained as the design target for a "good"
   /// chunk; the merge pass aims to lift undersized fragments toward this floor.
   /// </summary>
   private const int TargetMinTokens = 200;

   /// <summary>
   /// Upper edge of the preferred chunk size band. Chunks estimated above this are split (for
   /// methods) or have sibling-overlap suppressed, keeping every chunk comfortably under the
   /// embedding model's hard truncation cap.
   /// </summary>
   private const int TargetMaxTokens = 400;

   /// <summary>
   /// Words-to-tokens multiplier for the cheap token estimator. Empirically a BERT-family
   /// tokenizer emits roughly 1.2 tokens per whitespace-separated word of source code.
   /// </summary>
   private const float TokenEstimateFactor = 1.2f;

   /// <summary>
   /// Per-chunk cap on how much preview text is borrowed from the following sibling chunk.
   /// About 50 tokens captures the next member's signature plus its first statement, which is
   /// enough to keep phrases that straddle a chunk boundary discoverable.
   /// </summary>
   private const int OverlapTargetTokens = 50;

   /// <summary>
   /// Headroom guard for sibling overlap: chunks already this close to <see cref="TargetMaxTokens"/>
   /// are left alone, because appending overlap would push them into the tokenizer's truncation
   /// zone. Overlap is a recall boost, not a correctness requirement, so skipping the fat tail is
   /// the right trade-off.
   /// </summary>
   private const int OverlapBudgetCeiling = TargetMaxTokens - 20;

   #endregion

   #region Public Methods

   /// <summary>
   /// Parses the given source text and chunks it. This is the convenience entry point that
   /// builds the syntax tree itself; callers that already hold a parsed tree should use the
   /// overload that accepts one to avoid re-parsing.
   /// </summary>
   /// <param name="filePath">Origin path of the source, recorded on every chunk for context.</param>
   /// <param name="content">Full source text of the file.</param>
   /// <returns>The chunks, or an empty list when the content is blank.</returns>
   public List<CodeChunkDto> ChunkFile( string filePath, string content )
   {
      if ( string.IsNullOrWhiteSpace( content ) )
         return [];

      var syntaxTree = CSharpSyntaxTree.ParseText( content );
      return ChunkFile( filePath, content, syntaxTree );
   }

   /// <summary>
   /// Chunks a source file using a pre-parsed syntax tree. Walks every top-level type,
   /// emitting a class/interface shell chunk plus one or more member chunks per method,
   /// constructor, and non-trivial property, then merges undersized members and applies
   /// sibling overlap. Falls back to a single whole-file chunk when the source contains no
   /// type declarations at all.
   /// </summary>
   /// <param name="filePath">Origin path of the source, recorded on every chunk for context.</param>
   /// <param name="content">Full source text (used for the file-level fallback chunk).</param>
   /// <param name="syntaxTree">The already-parsed tree for <paramref name="content"/>.</param>
   /// <returns>The chunks, or an empty list when the content is blank.</returns>
   public List<CodeChunkDto> ChunkFile( string filePath, string content, SyntaxTree syntaxTree )
   {
      if ( string.IsNullOrWhiteSpace( content ) )
         return [];

      var root = syntaxTree.GetCompilationUnitRoot();
      var lines = content.Split( '\n' );
      var chunks = new List<CodeChunkDto>();

      var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();

      // No types at all (a script, a file of only usings, etc.): index the whole file as one chunk.
      if ( typeDeclarations.Count == 0 )
      {
         chunks.Add( MakeFileChunk( filePath, content, lines ) );
         return chunks;
      }

      foreach ( var typeDeclaration in typeDeclarations )
      {
         // Nested types are covered by their parent's walk, so skip them here.
         if ( typeDeclaration.Parent is TypeDeclarationSyntax )
            continue;

         ChunkTopLevelType( filePath, typeDeclaration, root, lines, chunks );
      }

      // Safety net: if the type walk somehow produced nothing usable, index the whole file.
      if ( chunks.Count == 0 )
         chunks.Add( MakeFileChunk( filePath, content, lines ) );

      return chunks;
   }

   /// <summary>
   /// Alias entry point that chunks arbitrary content under a caller-supplied identifier.
   /// Some callers key their sources by a logical identifier rather than a filesystem path;
   /// this simply forwards to <see cref="ChunkFile(string,string)"/> using that identifier as
   /// the file path.
   /// </summary>
   /// <param name="identifier">Logical id for the source, used in place of a file path.</param>
   /// <param name="content">Full source text to chunk.</param>
   public List<CodeChunkDto> Chunk( string identifier, string content )
   {
      return ChunkFile( identifier, content );
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// Emits all chunks for a single top-level type into <paramref name="chunks"/>: the shell
   /// chunk (interface, or class/struct/record with its fields and signatures), followed by the
   /// merged, overlap-augmented member chunks. Extracted from the main loop to keep the public
   /// entry point readable and each unit single-purpose.
   /// </summary>
   private static void ChunkTopLevelType(
      string filePath, TypeDeclarationSyntax typeDeclaration,
      CompilationUnitSyntax root, string[] lines, List<CodeChunkDto> chunks )
   {
      var namespaceName = GetNamespace( typeDeclaration );
      var classContext = GetClassContext( typeDeclaration );

      // Interfaces have no bodies to split, so we index the whole declaration as one chunk.
      if ( typeDeclaration is InterfaceDeclarationSyntax interfaceDeclaration )
      {
         chunks.Add( MakeInterfaceChunk( filePath, interfaceDeclaration, namespaceName, lines ) );
         return;
      }

      // Class/struct/record: a shell chunk with declaration + fields + constants + auto-props,
      // then one chunk per member body.
      chunks.Add( MakeClassChunk( filePath, typeDeclaration, namespaceName, root, lines ) );

      var memberChunks = BuildMemberChunks( filePath, typeDeclaration, namespaceName, classContext, lines );

      // Merge adjacent tiny members so we don't spend an embedding on trivial fragments.
      var mergedMembers = MergeUndersized( memberChunks, filePath, namespaceName, classContext );

      // Append a short preview of chunk N+1 onto chunk N so a query matching the seam between
      // two members (the tail of A plus the head of B) still lands on a chunk. See
      // ApplySiblingOverlap for the budget guard that skips already-fat chunks.
      ApplySiblingOverlap( mergedMembers );

      chunks.AddRange( mergedMembers );
   }

   /// <summary>
   /// Produces the raw (pre-merge) member chunks for a type: one entry per constructor, method,
   /// and non-trivial property. A single member yields more than one chunk only when an oversized
   /// method gets split at statement boundaries. Generated-DTO properties are deliberately
   /// excluded here because their boilerplate bodies are folded into the class shell instead.
   /// </summary>
   private static List<CodeChunkDto> BuildMemberChunks(
      string filePath, TypeDeclarationSyntax typeDeclaration,
      string namespaceName, string classContext, string[] lines )
   {
      var memberChunks = new List<CodeChunkDto>();

      foreach ( var member in typeDeclaration.Members )
      {
         IReadOnlyList<CodeChunkDto>? chunksFromMember = member switch
         {
            ConstructorDeclarationSyntax constructor =>
               MakeMemberChunk( filePath, "constructor", constructor.Identifier.Text, constructor, namespaceName, classContext, lines ),

            MethodDeclarationSyntax method =>
               MakeMemberChunk( filePath, "method", method.Identifier.Text, method, namespaceName, classContext, lines ),

            PropertyDeclarationSyntax property when HasNonTrivialBody( property ) && !IsGeneratedDto( filePath ) =>
               MakeMemberChunk( filePath, "property", property.Identifier.Text, property, namespaceName, classContext, lines ),

            _ => null
         };

         if ( chunksFromMember is not null )
            memberChunks.AddRange( chunksFromMember );
      }

      return memberChunks;
   }

   /// <summary>
   /// Builds the whole-file fallback chunk used when a source has no type declarations, or when
   /// the type walk unexpectedly yields nothing. The entire file becomes one chunk spanning all
   /// lines.
   /// </summary>
   private static CodeChunkDto MakeFileChunk( string filePath, string content, string[] lines )
   {
      return new CodeChunkDto
      {
         FilePath = filePath,
         ChunkType = "file",
         ChunkName = Path.GetFileNameWithoutExtension( filePath ),
         Content = content,
         StartLine = 1,
         EndLine = lines.Length
      };
   }

   /// <summary>
   /// Builds a chunk for an interface, capturing the full declaration (including its member
   /// signatures and any leading comments). Interfaces are small and body-free, so they're never
   /// split.
   /// </summary>
   private static CodeChunkDto MakeInterfaceChunk( string filePath, InterfaceDeclarationSyntax interfaceDeclaration, string namespaceName, string[] lines )
   {
      var span = interfaceDeclaration.GetLocation().GetLineSpan();
      var startLine = span.StartLinePosition.Line + 1;
      var endLine = span.EndLinePosition.Line + 1;
      var content = GetTextWithLeadingTrivia( interfaceDeclaration );

      return new CodeChunkDto
      {
         FilePath = filePath,
         ChunkType = "interface",
         ChunkName = interfaceDeclaration.Identifier.Text,
         Content = content,
         StartLine = startLine,
         EndLine = endLine,
         Signature = $"interface {interfaceDeclaration.Identifier.Text}{interfaceDeclaration.BaseList}",
         Namespace = namespaceName,
         ClassName = interfaceDeclaration.Identifier.Text
      };
   }

   /// <summary>
   /// Builds the "shell" chunk for a class, struct, or record: the reconstructed file usings and
   /// namespace, the type declaration line (with its XML docs), and the type's fields, constants,
   /// auto-properties, and nested enums. Method bodies are intentionally omitted, since each is
   /// indexed as its own member chunk. This gives semantic search a compact structural view of
   /// the type without duplicating body text.
   /// </summary>
   private static CodeChunkDto MakeClassChunk(
      string filePath, TypeDeclarationSyntax typeDeclaration, string namespaceName,
      CompilationUnitSyntax root, string[] lines )
   {
      var className = typeDeclaration.Identifier.Text;
      var builder = new System.Text.StringBuilder();

      AppendUsingsAndNamespace( builder, typeDeclaration, root );
      AppendTypeDeclarationHeader( builder, typeDeclaration );
      AppendShellMembers( builder, typeDeclaration, filePath );
      builder.AppendLine( "}" );

      var span = typeDeclaration.GetLocation().GetLineSpan();
      var startLine = span.StartLinePosition.Line + 1;
      var endLine = span.EndLinePosition.Line + 1;

      return new CodeChunkDto
      {
         FilePath = filePath,
         ChunkType = "class",
         ChunkName = className,
         Content = builder.ToString(),
         StartLine = startLine,
         EndLine = endLine,
         Namespace = namespaceName,
         ClassName = className,
         ParentContext = GetClassContext( typeDeclaration )
      };
   }

   /// <summary>
   /// Writes the file's using directives and the enclosing namespace opener into the class-shell
   /// builder. File-scoped namespaces emit a single terminated line; block-scoped namespaces open
   /// a brace (the shell doesn't bother closing it, since the text is embedding fodder, not
   /// compilable source).
   /// </summary>
   private static void AppendUsingsAndNamespace(
      System.Text.StringBuilder builder, TypeDeclarationSyntax typeDeclaration, CompilationUnitSyntax root )
   {
      foreach ( var usingDirective in root.Usings )
         builder.AppendLine( usingDirective.ToFullString().TrimEnd() );

      if ( root.Usings.Count > 0 )
         builder.AppendLine();

      var namespaceNode = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
      if ( namespaceNode is FileScopedNamespaceDeclarationSyntax fileScopedNamespace )
      {
         builder.AppendLine( $"namespace {fileScopedNamespace.Name};" );
         builder.AppendLine();
      }
      else if ( namespaceNode is NamespaceDeclarationSyntax blockNamespace )
      {
         builder.AppendLine( $"namespace {blockNamespace.Name}" );
         builder.AppendLine( "{" );
      }
   }

   /// <summary>
   /// Writes the type's XML doc comments (if any) followed by its declaration line and the opening
   /// brace into the class-shell builder.
   /// </summary>
   private static void AppendTypeDeclarationHeader(
      System.Text.StringBuilder builder, TypeDeclarationSyntax typeDeclaration )
   {
      var leadingDocComments = typeDeclaration.GetLeadingTrivia()
         .Where( t => t.IsKind( SyntaxKind.SingleLineDocumentationCommentTrivia )
                   || t.IsKind( SyntaxKind.MultiLineDocumentationCommentTrivia ) )
         .Select( t => t.ToFullString() );
      foreach ( var comment in leadingDocComments )
         builder.Append( comment );

      builder.AppendLine( GetTypeDeclarationLine( typeDeclaration ) );
      builder.AppendLine( "{" );
   }

   /// <summary>
   /// Writes the type's non-method members into the class-shell builder: field and constant
   /// declarations, auto-properties, and nested enums verbatim. For generated-DTO files the
   /// property bodies are boilerplate, so those properties are reduced to a bare
   /// { get; set; } signature.
   /// </summary>
   private static void AppendShellMembers(
      System.Text.StringBuilder builder, TypeDeclarationSyntax typeDeclaration, string filePath )
   {
      foreach ( var member in typeDeclaration.Members )
      {
         switch ( member )
         {
            case FieldDeclarationSyntax field:
               builder.AppendLine( $"    {field.ToFullString().Trim()}" );
               break;

            case PropertyDeclarationSyntax property when !HasNonTrivialBody( property ):
               builder.AppendLine( $"    {property.ToFullString().Trim()}" );
               break;

            // Generated DTO properties have boilerplate tracking bodies; keep only the signature.
            case PropertyDeclarationSyntax property when IsGeneratedDto( filePath ):
               builder.AppendLine( $"    {property.Modifiers} {property.Type} {property.Identifier} {{ get; set; }}" );
               break;

            case EnumDeclarationSyntax enumDeclaration:
               builder.AppendLine( $"    {enumDeclaration.ToFullString().Trim()}" );
               break;
         }
      }
   }

   /// <summary>
   /// Builds the chunk(s) for a single member. Normally this is exactly one chunk carrying the
   /// context prefix plus the member text. When a method body is estimated over
   /// <see cref="TargetMaxTokens"/> it's split at statement boundaries into multiple parts so no
   /// part of a large method disappears behind the embedding truncation cap; if the method can't
   /// be split (e.g. one giant expression), we fall through to the single-chunk path.
   /// </summary>
   private static IReadOnlyList<CodeChunkDto> MakeMemberChunk(
      string filePath, string chunkType, string name,
      MemberDeclarationSyntax member, string namespaceName, string classContext, string[] lines )
   {
      var span = member.GetLocation().GetLineSpan();
      var startLine = span.StartLinePosition.Line + 1;
      var endLine = span.EndLinePosition.Line + 1;

      var prefix = BuildContextPrefix( filePath, namespaceName, classContext, member );
      var memberText = GetTextWithLeadingTrivia( member );
      var fullContent = prefix + memberText;

      // Oversized method with a real body: try to split it at top-level statement boundaries.
      if ( EstimateTokens( fullContent ) > TargetMaxTokens
           && member is BaseMethodDeclarationSyntax { Body: { } methodBody } )
      {
         var split = SplitOversizedMethod(
            filePath, chunkType, name, member, methodBody, prefix, startLine, namespaceName, classContext );
         if ( split.Count > 1 )
            return split;
         // Couldn't split usefully; fall through and emit it whole.
      }

      return new[]
      {
         new CodeChunkDto
         {
            FilePath = filePath,
            ChunkType = chunkType,
            ChunkName = name,
            Content = fullContent,
            StartLine = startLine,
            EndLine = endLine,
            Signature = ExtractSignature( member ),
            Namespace = namespaceName,
            ClassName = GetEnclosingClassName( member ),
            ParentContext = classContext
         }
      };
   }

   /// <summary>
   /// Splits an oversized method into multiple chunks at top-level statement boundaries inside the
   /// method body. Each resulting chunk carries the context prefix, the method's signature line,
   /// a "(part N/M)" marker, and a contiguous group of statements. The embedding text does not
   /// need to be valid C#; it only needs to be useful for semantic similarity, so we don't try to
   /// balance braces. Returns an empty list when the body has no statements or when only a single
   /// group results (in which case the caller emits the method whole).
   /// </summary>
   private static IReadOnlyList<CodeChunkDto> SplitOversizedMethod(
      string filePath, string chunkType, string name,
      MemberDeclarationSyntax member, BlockSyntax methodBody,
      string prefix, int startLine, string namespaceName, string classContext )
   {
      var signatureHeader = ExtractSignature( member );

      // The header line lets the embedding model see "this is the body of method X" even on
      // parts 2..N, where the original declaration line isn't repeated.
      var headerLine = $"// Method signature: {signatureHeader}\n";

      var statements = methodBody.Statements;
      if ( statements.Count == 0 )
         return Array.Empty<CodeChunkDto>();

      var groups = GroupStatementsByBudget( statements, prefix, headerLine );

      // Only one group means splitting bought us nothing; let the caller emit the method whole.
      if ( groups.Count <= 1 )
         return Array.Empty<CodeChunkDto>();

      return BuildSplitChunks(
         groups, filePath, chunkType, name, member, prefix, headerLine, signatureHeader, namespaceName, classContext );
   }

   /// <summary>
   /// Greedily packs a method's statements into contiguous groups that each fit a per-part token
   /// budget (the max chunk budget minus the prefix and signature-header overhead). Statements are
   /// always taken whole; a single statement larger than the budget (e.g. one giant LINQ chain)
   /// becomes its own oversized group rather than being split, since a fat chunk beats a dropped
   /// statement. Each group records its first and last source line.
   /// </summary>
   private static List<(List<StatementSyntax> Statements, int StartLine, int EndLine)> GroupStatementsByBudget(
      SyntaxList<StatementSyntax> statements, string prefix, string headerLine )
   {
      // Reserve room for the repeated prefix and header on every part, with a little slack.
      var overheadTokens = EstimateTokens( prefix ) + EstimateTokens( headerLine ) + 20;
      var perPartBudget = Math.Max( 150, TargetMaxTokens - overheadTokens );

      var groups = new List<(List<StatementSyntax> Statements, int StartLine, int EndLine)>();
      var current = new List<StatementSyntax>();
      var currentTokens = 0;

      foreach ( var statement in statements )
      {
         var statementTokens = EstimateTokens( statement.GetText().ToString() );

         // Adding this statement would overflow the budget and we already have content: flush.
         // A single statement is never split across parts, even when it's individually oversized.
         if ( current.Count > 0 && currentTokens + statementTokens > perPartBudget )
         {
            groups.Add( SnapshotGroup( current ) );
            current.Clear();
            currentTokens = 0;
         }

         current.Add( statement );
         currentTokens += statementTokens;
      }

      if ( current.Count > 0 )
         groups.Add( SnapshotGroup( current ) );

      return groups;
   }

   /// <summary>
   /// Snapshots the currently accumulated statements into an immutable group tuple, capturing the
   /// first statement's start line and the last statement's end line (both 1-based).
   /// </summary>
   private static (List<StatementSyntax> Statements, int StartLine, int EndLine) SnapshotGroup(
      List<StatementSyntax> current )
   {
      var firstStart = current[0].GetLocation().GetLineSpan().StartLinePosition.Line + 1;
      var lastEnd = current[^1].GetLocation().GetLineSpan().EndLinePosition.Line + 1;
      return ( new List<StatementSyntax>( current ), firstStart, lastEnd );
   }

   /// <summary>
   /// Renders each statement group into a chunk: prefix, signature-header line, "(part N/M)"
   /// marker, then the group's statement text. Part names are suffixed with "~partNofM" so the
   /// pieces of one method stay distinguishable in search results.
   /// </summary>
   private static IReadOnlyList<CodeChunkDto> BuildSplitChunks(
      List<(List<StatementSyntax> Statements, int StartLine, int EndLine)> groups,
      string filePath, string chunkType, string name, MemberDeclarationSyntax member,
      string prefix, string headerLine, string signatureHeader, string namespaceName, string classContext )
   {
      var totalParts = groups.Count;
      var result = new List<CodeChunkDto>( totalParts );
      var enclosingClass = GetEnclosingClassName( member );
      var partIndex = 1;

      foreach ( var (statements, groupStart, groupEnd) in groups )
      {
         var builder = new System.Text.StringBuilder();
         builder.Append( prefix );
         builder.Append( headerLine );
         builder.Append( "// (part " ).Append( partIndex ).Append( '/' ).Append( totalParts ).AppendLine( ")" );

         foreach ( var statement in statements )
         {
            var statementText = statement.GetText().ToString();
            builder.Append( statementText );
            if ( !statementText.EndsWith( '\n' ) )
               builder.AppendLine();
         }

         result.Add( new CodeChunkDto
         {
            FilePath = filePath,
            ChunkType = chunkType,
            ChunkName = totalParts > 1 ? $"{name}~part{partIndex}of{totalParts}" : name,
            Content = builder.ToString(),
            StartLine = groupStart,
            EndLine = groupEnd,
            Signature = signatureHeader,
            Namespace = namespaceName,
            ClassName = enclosingClass,
            ParentContext = classContext
         } );
         partIndex++;
      }

      return result;
   }

   /// <summary>
   /// Merges runs of adjacent undersized member chunks into a single combined chunk, so trivial
   /// members don't each burn an embedding. A member is undersized when its estimate falls below
   /// <see cref="MinTokenEstimate"/>. A lone undersized member keeps its original identity; only
   /// runs of two or more are actually combined.
   /// </summary>
   private static List<CodeChunkDto> MergeUndersized(
      List<CodeChunkDto> chunks, string filePath, string namespaceName, string classContext )
   {
      if ( chunks.Count == 0 )
         return chunks;

      var result = new List<CodeChunkDto>();
      var pending = new List<CodeChunkDto>();

      foreach ( var chunk in chunks )
      {
         if ( EstimateTokens( chunk.Content ) < MinTokenEstimate )
         {
            pending.Add( chunk );
         }
         else
         {
            FlushPendingTo( result, pending, filePath, namespaceName, classContext );
            pending.Clear();
            result.Add( chunk );
         }
      }

      FlushPendingTo( result, pending, filePath, namespaceName, classContext );

      return result;
   }

   /// <summary>
   /// Drains the accumulated undersized chunks into the result list. A single pending chunk is
   /// passed through unchanged (it keeps its original chunk type). Two or more are concatenated
   /// into one "members" chunk whose name joins the member names with '+' and whose span covers
   /// the first through last member.
   /// </summary>
   private static void FlushPendingTo(
      List<CodeChunkDto> result, List<CodeChunkDto> pending,
      string filePath, string namespaceName, string classContext )
   {
      if ( pending.Count == 0 )
         return;

      if ( pending.Count == 1 )
      {
         result.Add( pending[0] );
         return;
      }

      var mergedNames = string.Join( "+", pending.Select( p => p.ChunkName ) );
      result.Add( new CodeChunkDto
      {
         FilePath = filePath,
         ChunkType = "members",
         ChunkName = mergedNames,
         Content = string.Join( "\n\n", pending.Select( p => p.Content ) ),
         StartLine = pending[0].StartLine,
         EndLine = pending[^1].EndLine,
         Namespace = namespaceName,
         ClassName = pending[0].ClassName,
         ParentContext = classContext
      } );
   }

   /// <summary>
   /// Appends a short leading preview of chunk N+1 onto chunk N, in place, so semantic search
   /// doesn't lose phrases that straddle a chunk boundary. The preview is added as a
   /// comment-tagged tail block: visually easy to ignore but fully indexed by the embedding pass.
   /// Chunks already near the token cap are skipped, since the appended text would only be
   /// truncated server-side.
   /// </summary>
   private static void ApplySiblingOverlap( List<CodeChunkDto> chunks )
   {
      if ( chunks.Count < 2 )
         return;

      for ( int i = 0; i < chunks.Count - 1; i++ )
      {
         var current = chunks[i];
         var next = chunks[i + 1];

         // Already near the cap: adding overlap would push it into the truncation zone.
         if ( EstimateTokens( current.Content ) >= OverlapBudgetCeiling )
            continue;

         // Pull from raw content (which includes the next chunk's context prefix). The prefix
         // repetition is intentional: it surfaces the next member's signature in this chunk's
         // embedding window.
         var preview = ExtractLeadingPreview( next.Content, OverlapTargetTokens );
         if ( string.IsNullOrWhiteSpace( preview ) )
            continue;

         // CodeChunkDto is a plain class with settable properties, so mutate in place.
         current.Content = current.Content
            + "\n// --- next-chunk overlap (semantic continuity) ---\n"
            + preview;
      }
   }

   /// <summary>
   /// Returns up to <paramref name="maxTokens"/> estimated tokens of leading text from
   /// <paramref name="text"/>. Walks whitespace-separated words and stops when the estimate
   /// reaches the cap, then slices at the original character offset so the source formatting is
   /// preserved (a split-then-join would lose the original whitespace). Fast: no Roslyn re-parse.
   /// </summary>
   private static string ExtractLeadingPreview( string text, int maxTokens )
   {
      if ( string.IsNullOrEmpty( text ) )
         return string.Empty;

      var words = text.Split( [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries );
      var maxWords = (int)Math.Ceiling( maxTokens / TokenEstimateFactor );
      if ( maxWords >= words.Length )
         return text;

      // Walk char-by-char to find where the first maxWords words end, preserving formatting.
      var wordsSeen = 0;
      var inWord = false;
      for ( int idx = 0; idx < text.Length; idx++ )
      {
         var character = text[idx];
         var isSeparator = character == ' ' || character == '\t' || character == '\n' || character == '\r';
         if ( inWord && isSeparator )
         {
            inWord = false;
            wordsSeen++;
            if ( wordsSeen >= maxWords )
               return text[..idx];
         }
         else if ( !inWord && !isSeparator )
         {
            inWord = true;
         }
      }

      return text;
   }

   /// <summary>
   /// Builds the comment header prepended to every member chunk: the file path, namespace,
   /// enclosing class, and up to five of the type's field declarations. This context travels with
   /// the chunk so a search hit on a bare method still tells the reader where it lives and what
   /// state it operates on.
   /// </summary>
   private static string BuildContextPrefix(
      string filePath, string namespaceName, string classContext, MemberDeclarationSyntax member )
   {
      var builder = new System.Text.StringBuilder();
      builder.AppendLine( $"// File: {filePath}" );

      if ( !string.IsNullOrEmpty( namespaceName ) )
         builder.AppendLine( $"// Namespace: {namespaceName}" );

      if ( !string.IsNullOrEmpty( classContext ) )
         builder.AppendLine( $"// Class: {classContext}" );

      if ( member.Parent is TypeDeclarationSyntax parentType )
      {
         var fields = parentType.Members.OfType<FieldDeclarationSyntax>()
            .Select( f => f.ToFullString().Trim() )
            .ToList();

         if ( fields.Count > 0 )
         {
            // Cap at five fields to keep the prefix small; note the elision when there are more.
            var fieldSummary = string.Join( "; ", fields.Take( 5 ) );
            if ( fields.Count > 5 )
               fieldSummary += "; ...";
            builder.AppendLine( $"// Fields: {fieldSummary}" );
         }
      }

      builder.AppendLine();
      return builder.ToString();
   }

   /// <summary>
   /// Returns the dotted namespace name enclosing <paramref name="node"/>, or an empty string
   /// when the node lives in the global namespace.
   /// </summary>
   private static string GetNamespace( SyntaxNode node )
   {
      var namespaceNode = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
      return namespaceNode?.Name.ToString() ?? "";
   }

   /// <summary>
   /// Builds a human-readable descriptor of a type for the "// Class:" context line: the kind
   /// keyword (class/struct/record, including record struct), the name with its type parameters,
   /// and the base list when present.
   /// </summary>
   private static string GetClassContext( TypeDeclarationSyntax typeDeclaration )
   {
      var keyword = typeDeclaration switch
      {
         RecordDeclarationSyntax record => record.ClassOrStructKeyword.Text.Length > 0 ? $"record {record.ClassOrStructKeyword.Text}" : "record",
         StructDeclarationSyntax => "struct",
         _ => "class"
      };

      var name = typeDeclaration.Identifier.Text;
      var baseList = typeDeclaration.BaseList?.ToString() ?? "";
      var typeParameters = typeDeclaration.TypeParameterList?.ToString() ?? "";

      return string.IsNullOrEmpty( baseList )
         ? $"{name}{typeParameters}"
         : $"{name}{typeParameters} {baseList}";
   }

   /// <summary>
   /// Returns the name of the innermost type declaration enclosing <paramref name="node"/>, or an
   /// empty string when there isn't one.
   /// </summary>
   private static string GetEnclosingClassName( SyntaxNode node )
   {
      var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
      return type?.Identifier.Text ?? "";
   }

   /// <summary>
   /// Reconstructs a type's declaration line (modifiers, keyword, name with type parameters, and
   /// base list) as a single space-joined string, omitting any empty parts. Used for the opening
   /// line of the class-shell chunk.
   /// </summary>
   private static string GetTypeDeclarationLine( TypeDeclarationSyntax typeDeclaration )
   {
      var modifiers = typeDeclaration.Modifiers.ToString();
      var keyword = typeDeclaration.Keyword.Text;
      var name = typeDeclaration.Identifier.Text;
      var typeParameters = typeDeclaration.TypeParameterList?.ToString() ?? "";
      var baseList = typeDeclaration.BaseList?.ToString() ?? "";

      var parts = new[] { modifiers, keyword, $"{name}{typeParameters}", baseList }
         .Where( p => !string.IsNullOrWhiteSpace( p ) );
      return string.Join( " ", parts );
   }

   /// <summary>
   /// Extracts a compact one-line signature for a member (method, constructor, or property),
   /// stored on the chunk for display and filtering. Returns an empty string for member kinds we
   /// don't summarize.
   /// </summary>
   private static string ExtractSignature( MemberDeclarationSyntax member )
   {
      return member switch
      {
         MethodDeclarationSyntax method =>
            $"{method.Modifiers} {method.ReturnType} {method.Identifier}{method.TypeParameterList}{method.ParameterList}".Trim(),
         ConstructorDeclarationSyntax constructor =>
            $"{constructor.Modifiers} {constructor.Identifier}{constructor.ParameterList}".Trim(),
         PropertyDeclarationSyntax property =>
            $"{property.Modifiers} {property.Type} {property.Identifier}".Trim(),
         _ => ""
      };
   }

   /// <summary>
   /// Returns the node's source text with its leading documentation and ordinary comments
   /// preserved, but with any other leading whitespace trimmed. Keeping the doc/comment trivia
   /// means the chunk carries the member's own explanation into the embedding.
   /// </summary>
   private static string GetTextWithLeadingTrivia( SyntaxNode node )
   {
      var comments = node.GetLeadingTrivia()
         .Where( t => t.IsKind( SyntaxKind.SingleLineDocumentationCommentTrivia )
                   || t.IsKind( SyntaxKind.MultiLineDocumentationCommentTrivia )
                   || t.IsKind( SyntaxKind.SingleLineCommentTrivia )
                   || t.IsKind( SyntaxKind.MultiLineCommentTrivia ) );

      var commentText = string.Join( "", comments.Select( t => t.ToFullString() ) );
      var nodeText = node.ToFullString().TrimStart();

      return string.IsNullOrEmpty( commentText ) ? nodeText : commentText + nodeText;
   }

   /// <summary>
   /// Reports whether a property has real logic worth indexing on its own. Auto-properties
   /// (no accessor list, no expression body) are trivial; a property counts as non-trivial when
   /// it has an expression body or any accessor with a body.
   /// </summary>
   private static bool HasNonTrivialBody( PropertyDeclarationSyntax property )
   {
      if ( property.AccessorList is null )
         return property.ExpressionBody is not null;

      return property.AccessorList.Accessors.Any( a => a.Body is not null || a.ExpressionBody is not null );
   }

   /// <summary>
   /// Reports whether the file is a generated DTO (*.Generated.cs). Those files carry boilerplate
   /// property-tracking bodies (_RetrievedProperties.Contains/Add) on every property, so their
   /// properties are folded into the class shell as signatures rather than getting individual
   /// property chunks.
   /// </summary>
   internal static bool IsGeneratedDto( string filePath ) =>
      filePath.EndsWith( ".Generated.cs", StringComparison.OrdinalIgnoreCase );

   /// <summary>
   /// Cheap token estimator: counts whitespace-separated words and scales by
   /// <see cref="TokenEstimateFactor"/> to approximate a BERT-family tokenizer's output. Used
   /// throughout for budget decisions; it trades accuracy for speed since we only need a
   /// ballpark to keep chunks under the model's cap.
   /// </summary>
   internal static int EstimateTokens( string text )
   {
      if ( string.IsNullOrWhiteSpace( text ) )
         return 0;

      var wordCount = 0;
      var inWord = false;

      for ( int i = 0; i < text.Length; i++ )
      {
         if ( char.IsWhiteSpace( text[i] ) )
         {
            inWord = false;
         }
         else if ( !inWord )
         {
            wordCount++;
            inWord = true;
         }
      }

      return (int)( wordCount * TokenEstimateFactor );
   }

   #endregion
}
