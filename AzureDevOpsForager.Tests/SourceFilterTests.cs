using AzureDevOpsForager.Core.Services.Sources;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Covers <see cref="SourceFilterOptions.ShouldInclude"/> — the glob-driven include/exclude
/// gate that decides which repo files get indexed. Excludes take precedence over includes,
/// and a file with no matching include glob is dropped.
/// </summary>
public class SourceFilterTests
{
   /// <summary>The spec's baseline: include all .cs files, exclude anything under bin/ or obj/.</summary>
   private static SourceFilterOptions CsOnlyExcludingBuildOutput() => new()
   {
      IncludeGlobs = new List<string> { "**/*.cs" },
      ExcludeGlobs = new List<string> { "**/bin/**", "**/obj/**" }
   };

   [Theory]
   // A .cs file in a nested source folder — matches the include, no exclude hit.
   [InlineData( "src/App/Foo.cs", true )]
   // A .cs file at the repo root — "**/*.cs" also matches the zero-folder case.
   [InlineData( "Foo.cs", true )]
   // Under bin/ — excluded even though it's a .cs file.
   [InlineData( "src/bin/Debug/Foo.cs", false )]
   // Not a .cs file — no include glob matches, so it's dropped.
   [InlineData( "README.md", false )]
   // Under obj/ — excluded.
   [InlineData( "src/obj/x.cs", false )]
   public void ShouldInclude_CsOnlyExcludingBuildOutput_MatchesExpectation( string path, bool expected )
   {
      var options = CsOnlyExcludingBuildOutput();

      Assert.Equal( expected, options.ShouldInclude( path ) );
   }

   [Fact]
   public void ShouldInclude_BackslashPaths_AreNormalizedToForwardSlashes()
   {
      // The indexer feeds Windows-style paths; the filter normalizes '\' → '/' before matching.
      var options = CsOnlyExcludingBuildOutput();

      Assert.True( options.ShouldInclude( @"src\App\Foo.cs" ) );
      Assert.False( options.ShouldInclude( @"src\bin\Debug\Foo.cs" ) );
   }

   [Fact]
   public void ShouldInclude_ExcludeWinsOverInclude()
   {
      // A path that matches the include glob AND an exclude glob is excluded — exclude has priority.
      var options = CsOnlyExcludingBuildOutput();

      Assert.False( options.ShouldInclude( "bin/Generated.cs" ) );
   }

   [Fact]
   public void ShouldInclude_NoIncludeGlobs_IncludesEverythingNotExcluded()
   {
      // Empty include list means "include all" (subject only to excludes).
      var options = new SourceFilterOptions
      {
         IncludeGlobs = new List<string>(),
         ExcludeGlobs = new List<string> { "**/bin/**" }
      };

      Assert.True( options.ShouldInclude( "README.md" ) );
      Assert.True( options.ShouldInclude( "src/App/Foo.cs" ) );
      Assert.False( options.ShouldInclude( "src/bin/Debug/Foo.cs" ) );
   }
}
