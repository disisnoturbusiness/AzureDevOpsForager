using AzureDevOpsForager.Indexer.Indexing;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Covers <see cref="RoslynMetadataExtractor.Extract"/> — pure Roslyn syntax-tree metadata
/// extraction for a single C# file. Feeds one representative source string (namespace +
/// public class with a base class + method + property, plus a standalone enum) and asserts
/// the structural fields the indexer relies on for search.
/// </summary>
public class RoslynMetadataTests
{
   private const string Source = @"
using System;

namespace Sample.Domain
{
    public class Widget : WidgetBase
    {
        public string Name { get; set; }

        public int Compute( int x )
        {
            return x * 2;
        }
    }

    public enum WidgetState
    {
        Active,
        Inactive
    }
}
";

   [Fact]
   public void Extract_ReadsNamespaceClassBaseAndKind()
   {
      var m = RoslynMetadataExtractor.Extract( Source );

      Assert.Equal( "Sample.Domain", m.Namespace );
      // ClassName is the first type declaration — the class, not the enum.
      Assert.Equal( "Widget", m.ClassName );
      Assert.Equal( "WidgetBase", m.BaseClass );
      Assert.Equal( "class", m.FileType );
   }

   [Fact]
   public void Extract_CollectsMethodAndPropertyNames()
   {
      var m = RoslynMetadataExtractor.Extract( Source );

      // MethodNames / PropertyNames are space-joined token lists.
      Assert.Contains( "Compute", m.MethodNames.Split( ' ' ) );
      Assert.Contains( "Name", m.PropertyNames.Split( ' ' ) );
   }

   [Fact]
   public void Extract_CapturesEnumNameAndMembers()
   {
      var m = RoslynMetadataExtractor.Extract( Source );

      // EnumValues is formatted as "EnumName(Member1,Member2)".
      Assert.Contains( "WidgetState", m.EnumValues );
      Assert.Contains( "Active", m.EnumValues );
      Assert.Contains( "Inactive", m.EnumValues );
   }

   [Fact]
   public void Extract_ClassNames_ListsTypeDeclarationsButNotEnums()
   {
      var m = RoslynMetadataExtractor.Extract( Source );

      // ClassNames is derived from TypeDeclarationSyntax (class/struct/record/interface).
      // In Roslyn an enum is an EnumDeclarationSyntax — a BaseTypeDeclarationSyntax, NOT a
      // TypeDeclarationSyntax — so enum names never appear here. Enum names are surfaced via
      // EnumValues instead (see Extract_CapturesEnumNameAndMembers). Asserting the real contract.
      var names = m.ClassNames.Split( ' ' );
      Assert.Contains( "Widget", names );
      Assert.DoesNotContain( "WidgetState", names );
   }

   [Fact]
   public void Extract_EmptyInput_ReturnsEmptyMetadata()
   {
      // Whitespace-only content short-circuits to an all-empty FileMetadata (no throw).
      var m = RoslynMetadataExtractor.Extract( "   " );

      Assert.Equal( "", m.Namespace );
      Assert.Equal( "", m.ClassName );
      Assert.Equal( "", m.MethodNames );
   }
}
