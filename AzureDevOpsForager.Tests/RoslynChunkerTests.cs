using AzureDevOpsForager.Indexer.Indexing;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Covers <see cref="RoslynChunker.ChunkFile(string, string)"/> — the syntax-aware chunker
/// that splits a source file into context-rich <see cref="CodeChunkDto"/> pieces for
/// embedding. Feeds a small two-method class and asserts the chunker produces a class chunk,
/// surfaces the member names, and populates sensible line spans.
/// </summary>
public class RoslynChunkerTests
{
   private const string Source = @"
namespace Sample.Domain
{
    public class Calculator
    {
        public int Alpha( int x )
        {
            return x + 1;
        }

        public int Beta( int y )
        {
            return y - 1;
        }
    }
}
";

   [Fact]
   public void ChunkFile_TwoMethodClass_ProducesAtLeastOneChunk()
   {
      var chunks = new RoslynChunker().ChunkFile( "Foo.cs", Source );

      Assert.NotEmpty( chunks );
   }

   [Fact]
   public void ChunkFile_ProducesAClassChunkForTheType()
   {
      var chunks = new RoslynChunker().ChunkFile( "Foo.cs", Source );

      var classChunk = Assert.Single( chunks, c => c.ChunkType == "class" );
      Assert.Equal( "Calculator", classChunk.ChunkName );
      Assert.Equal( "Sample.Domain", classChunk.Namespace );
   }

   [Fact]
   public void ChunkFile_SurfacesBothMemberNames()
   {
      var chunks = new RoslynChunker().ChunkFile( "Foo.cs", Source );

      // Both methods are tiny, so the chunker merges them into a single "members" chunk whose
      // name joins the member names (e.g. "Alpha+Beta"). Either way, both names appear across
      // the chunk names — assert on the concatenation so we're robust to the merge decision.
      var allNames = string.Join( " ", chunks.Select( c => c.ChunkName ) );
      Assert.Contains( "Alpha", allNames );
      Assert.Contains( "Beta", allNames );
   }

   [Fact]
   public void ChunkFile_PopulatesSensibleLineSpans()
   {
      var chunks = new RoslynChunker().ChunkFile( "Foo.cs", Source );

      Assert.All( chunks, c =>
      {
         Assert.True( c.StartLine >= 1, $"StartLine should be 1-based, was {c.StartLine} for '{c.ChunkName}'" );
         Assert.True( c.EndLine >= c.StartLine, $"EndLine ({c.EndLine}) should be >= StartLine ({c.StartLine}) for '{c.ChunkName}'" );
         Assert.False( string.IsNullOrWhiteSpace( c.ChunkName ), "ChunkName should be populated" );
         Assert.False( string.IsNullOrWhiteSpace( c.Content ), "Content should be populated" );
      } );
   }

   [Fact]
   public void ChunkFile_EmptyContent_ReturnsNoChunks()
   {
      var chunks = new RoslynChunker().ChunkFile( "Foo.cs", "   " );

      Assert.Empty( chunks );
   }
}
