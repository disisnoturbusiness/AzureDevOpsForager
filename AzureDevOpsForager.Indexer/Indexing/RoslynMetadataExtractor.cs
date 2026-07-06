#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AzureDevOpsForager.Indexer.Indexing;

/// <summary>
/// Language-structural metadata for a single C# source file. Every value here is derived purely
/// from the Roslyn syntax tree, so nothing in this class encodes any domain or vendor concept;
/// it is a flat, search-friendly projection of "what shapes live in this file" (types, members,
/// modifiers, referenced type names, and so on). The indexer stores these strings so that a code
/// search can match on structural facts, not just raw text.
/// </summary>
public class FileMetadata
{
   #region Data Members

   /// <summary>
   /// The Roslyn type-kind of the file's primary declaration (class / interface / enum / struct /
   /// record / static class / abstract class). Used to bucket search results by the kind of thing
   /// the file defines.
   /// </summary>
   public string FileType = "";

   /// <summary>The declaring namespace of the file's first namespace declaration, if any.</summary>
   public string Namespace = "";

   /// <summary>The identifier of the file's first (primary) type declaration.</summary>
   public string ClassName = "";

   /// <summary>The primary type's base class, if it declares one (interfaces are excluded, see <see cref="Interfaces"/>).</summary>
   public string BaseClass = "";

   /// <summary>Comma-separated list of interfaces the primary type implements.</summary>
   public string Interfaces = "";

   /// <summary>The primary type's declared modifiers (e.g. "public static", "internal abstract").</summary>
   public string ClassModifiers = "";

   /// <summary>Space-separated identifiers of every type declaration in the file (class/struct/record/interface).</summary>
   public string ClassNames = "";

   /// <summary>Space-separated names of the file's public properties.</summary>
   public string PropertyNames = "";

   /// <summary>Space-separated names of every method declared in the file.</summary>
   public string MethodNames = "";

   /// <summary>Pipe-separated "Type Name" pairs for the file's public properties.</summary>
   public string Properties = "";

   /// <summary>Pipe-separated constructor signatures (modifiers, name, and parameter list).</summary>
   public string Constructors = "";

   /// <summary>Space-separated names of methods carrying the <c>override</c> modifier.</summary>
   public string OverriddenMethods = "";

   /// <summary>Space-separated names of methods carrying the <c>abstract</c> modifier.</summary>
   public string AbstractMethods = "";

   /// <summary>Space-separated names of methods carrying the <c>virtual</c> modifier.</summary>
   public string VirtualMethods = "";

   /// <summary>Space-separated names of methods carrying the <c>async</c> modifier.</summary>
   public string AsyncMethods = "";

   /// <summary>Enum names with their members, formatted as "EnumName(Member1,Member2)" and pipe-separated.</summary>
   public string EnumValues = "";

   /// <summary>Pipe-separated, sorted, distinct list of the file's using directives.</summary>
   public string Usings = "";

   /// <summary>Space-separated distinct attribute names applied anywhere in the file.</summary>
   public string Attributes = "";

   /// <summary>Pipe-separated names of the file's <c>#region</c> directives.</summary>
   public string Regions = "";

   /// <summary>Space-separated names of the file's <c>const</c> fields.</summary>
   public string Constants = "";

   /// <summary>Space-separated names of the file's non-const <c>static</c> fields.</summary>
   public string StaticFields = "";

   /// <summary>Space-separated SQL keywords found in the raw text (a cheap "does this file touch SQL" signal).</summary>
   public string SqlOperations = "";

   /// <summary>Pipe-separated distinct <c>Dictionary&lt;,&gt;</c> constructions used in the file.</summary>
   public string Dictionaries = "";

   /// <summary>Space-separated distinct generic type identifiers referenced in the file.</summary>
   public string GenericTypes = "";

   /// <summary>Space-separated distinct event names (both event properties and event fields).</summary>
   public string Events = "";

   /// <summary>Space-separated distinct delegate type names declared in the file.</summary>
   public string Delegates = "";

   /// <summary>Space-separated, sorted set of simple type names the file references across the syntax tree.</summary>
   public string ReferencedTypes = "";

   #endregion
}

/// <summary>
/// Extracts <see cref="FileMetadata"/> from C# source text using Roslyn. This is deliberately a
/// pure syntax-tree analysis: the source is parsed exactly once and no semantic model or full
/// compilation is built, which keeps extraction fast and dependency-free (it works on a lone file
/// without needing the rest of the project to compile). Everything it reports is structural, so
/// it stays generic across any C# codebase the indexer is pointed at.
/// </summary>
public static class RoslynMetadataExtractor
{
   #region Data Members

   /// <summary>
   /// The SQL keywords scanned for in raw file text. A hit only means the token appears somewhere,
   /// so this is a coarse "this file probably contains SQL" heuristic, not a parser.
   /// </summary>
   private static readonly string[] SqlKeywords =
      { "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "JOIN", "WHERE", "GROUP BY", "ORDER BY" };

   #endregion

   #region Public Methods

   /// <summary>
   /// Parses the given C# source and returns its structural metadata. Whitespace-only input returns
   /// empty metadata, and any parse failure is swallowed so an unparseable file yields empty metadata
   /// rather than throwing; the indexer should never blow up on one bad file.
   /// </summary>
   public static FileMetadata Extract( string content )
   {
      var metadata = new FileMetadata();
      if( string.IsNullOrWhiteSpace( content ) )
         return metadata;

      CompilationUnitSyntax root;
      try
      {
         root = CSharpSyntaxTree.ParseText( content ).GetCompilationUnitRoot();
      }
      catch
      {
         return metadata;   // unparseable file, leave metadata empty rather than throw
      }

      PopulateTypeInfo( metadata, root );
      PopulateMemberInfo( metadata, root );
      PopulateStructuralInfo( metadata, root );
      metadata.SqlOperations = ExtractSqlOperations( content );

      return metadata;
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// Fills the file-level and primary-type facts: kind, namespace, the primary type's modifiers,
   /// name, base class and interfaces, and the full list of type-declaration names.
   /// </summary>
   private static void PopulateTypeInfo( FileMetadata metadata, CompilationUnitSyntax root )
   {
      metadata.FileType = DeriveFileType( root );
      metadata.Namespace = ExtractNamespace( root );

      var ( modifiers, className, baseClass, interfaces ) = ExtractClassInfo( root );
      metadata.ClassModifiers = modifiers;
      metadata.ClassName = className;
      metadata.BaseClass = baseClass;
      metadata.Interfaces = interfaces;
      metadata.ClassNames = ExtractAllClassNames( root );
   }

   /// <summary>
   /// Fills the member-level facts: property and method names, full property signatures,
   /// constructors, and the four modifier-based method buckets (override/abstract/virtual/async).
   /// </summary>
   private static void PopulateMemberInfo( FileMetadata metadata, CompilationUnitSyntax root )
   {
      metadata.PropertyNames = ExtractPropertyNames( root );
      metadata.MethodNames = ExtractMethodNames( root );
      metadata.Properties = ExtractAllProperties( root );
      metadata.Constructors = ExtractConstructors( root );
      metadata.OverriddenMethods = ExtractMethodsWithModifier( root, SyntaxKind.OverrideKeyword );
      metadata.AbstractMethods = ExtractMethodsWithModifier( root, SyntaxKind.AbstractKeyword );
      metadata.VirtualMethods = ExtractMethodsWithModifier( root, SyntaxKind.VirtualKeyword );
      metadata.AsyncMethods = ExtractMethodsWithModifier( root, SyntaxKind.AsyncKeyword );
   }

   /// <summary>
   /// Fills the remaining structural facts that do not fit the type/member split: enums, usings,
   /// attributes, regions, constants, static fields, dictionaries, generics, events, delegates,
   /// and the referenced-type graph.
   /// </summary>
   private static void PopulateStructuralInfo( FileMetadata metadata, CompilationUnitSyntax root )
   {
      metadata.EnumValues = ExtractEnumValues( ExtractEnums( root ) );
      metadata.Usings = ExtractUsings( root );
      metadata.Attributes = ExtractAttributes( root );
      metadata.Regions = ExtractRegions( root );
      metadata.Constants = ExtractConstants( root );
      metadata.StaticFields = ExtractStaticFields( root );
      metadata.Dictionaries = ExtractDictionaries( root );
      metadata.GenericTypes = ExtractGenericTypes( root );
      metadata.Events = ExtractEvents( root );
      metadata.Delegates = ExtractDelegates( root );
      metadata.ReferencedTypes = ExtractReferencedTypes( root );
   }

   /// <summary>
   /// Classifies the file by the kind of its first type declaration. Uses the Roslyn type-kind
   /// directly (rather than path or base-class matching) so the result stays generic across any
   /// codebase. Static and abstract classes are called out separately from a plain class.
   /// </summary>
   private static string DeriveFileType( CompilationUnitSyntax root )
   {
      var first = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
      return first switch
      {
         InterfaceDeclarationSyntax => "interface",
         EnumDeclarationSyntax => "enum",
         StructDeclarationSyntax => "struct",
         RecordDeclarationSyntax recordDecl => recordDecl.ClassOrStructKeyword.IsKind( SyntaxKind.StructKeyword ) ? "record struct" : "record",
         ClassDeclarationSyntax classDecl when classDecl.Modifiers.Any( SyntaxKind.StaticKeyword ) => "static class",
         ClassDeclarationSyntax classDecl when classDecl.Modifiers.Any( SyntaxKind.AbstractKeyword ) => "abstract class",
         ClassDeclarationSyntax => "class",
         _ => "other"
      };
   }

   /// <summary>Returns the name of the file's first namespace declaration, or empty if it has none.</summary>
   private static string ExtractNamespace( CompilationUnitSyntax root )
   {
      var namespaceDecl = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
      return namespaceDecl?.Name.ToString() ?? "";
   }

   /// <summary>
   /// Splits the primary type's base list into a single base class plus its interfaces. Because
   /// there is no semantic model, it leans on the universal C# naming convention: a base type is
   /// treated as an interface when its name is "I" followed by an uppercase letter, and the first
   /// remaining base type is taken as the base class.
   /// </summary>
   private static ( string modifiers, string className, string baseClass, string interfaces ) ExtractClassInfo( CompilationUnitSyntax root )
   {
      var typeDecl = root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();
      if( typeDecl is null )
         return ("", "", "", "");

      var modifiers = typeDecl.Modifiers.ToString();
      var className = typeDecl.Identifier.Text;

      if( typeDecl.BaseList is null )
         return (modifiers, className, "", "");

      var baseTypes = typeDecl.BaseList.Types.Select( baseType => baseType.Type.ToString() ).ToList();

      static bool LooksLikeInterface( string name ) => name.Length >= 2 && name[0] == 'I' && char.IsUpper( name[1] );

      var baseClass = "";
      var interfaceList = new List<string>();
      foreach( var baseType in baseTypes )
      {
         if( baseClass == "" && !LooksLikeInterface( baseType ) )
            baseClass = baseType;
         else
            interfaceList.Add( baseType );
      }

      return (modifiers, className, baseClass, string.Join( ", ", interfaceList ));
   }

   /// <summary>Space-separated distinct identifiers of every type declaration (class/struct/record/interface) in the file.</summary>
   private static string ExtractAllClassNames( CompilationUnitSyntax root ) =>
      string.Join( " ", root.DescendantNodes().OfType<TypeDeclarationSyntax>()
         .Select( typeDecl => typeDecl.Identifier.Text ).Distinct() );

   /// <summary>Space-separated distinct names of the file's public properties.</summary>
   private static string ExtractPropertyNames( CompilationUnitSyntax root ) =>
      string.Join( " ", root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
         .Where( property => property.Modifiers.Any( SyntaxKind.PublicKeyword ) )
         .Select( property => property.Identifier.Text ).Distinct() );

   /// <summary>Space-separated distinct names of every method declared in the file.</summary>
   private static string ExtractMethodNames( CompilationUnitSyntax root ) =>
      string.Join( " ", root.DescendantNodes().OfType<MethodDeclarationSyntax>()
         .Select( method => method.Identifier.Text ).Distinct() );

   /// <summary>Pipe-separated "Type Name" signatures for the file's public properties.</summary>
   private static string ExtractAllProperties( CompilationUnitSyntax root ) =>
      string.Join( " | ", root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
         .Where( property => property.Modifiers.Any( SyntaxKind.PublicKeyword ) )
         .Select( property => $"{property.Type} {property.Identifier.Text}" ) );

   /// <summary>Pipe-separated constructor signatures (modifiers, name, parameter list), whitespace-trimmed.</summary>
   private static string ExtractConstructors( CompilationUnitSyntax root ) =>
      string.Join( " | ", root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
         .Select( constructor => $"{constructor.Modifiers} {constructor.Identifier}{constructor.ParameterList}".Trim() ) );

   /// <summary>
   /// Space-separated distinct names of methods carrying the given modifier. Backs the
   /// override/abstract/virtual/async buckets from a single reusable query.
   /// </summary>
   private static string ExtractMethodsWithModifier( CompilationUnitSyntax root, SyntaxKind modifier ) =>
      string.Join( " ", root.DescendantNodes().OfType<MethodDeclarationSyntax>()
         .Where( method => method.Modifiers.Any( modifier ) )
         .Select( method => method.Identifier.Text ).Distinct() );

   /// <summary>
   /// Collects each enum and its member names. The conventional placeholder members "None" and
   /// "Default" are dropped because they carry no descriptive signal for search, and enums left with
   /// no meaningful members are omitted entirely.
   /// </summary>
   private static Dictionary<string, List<string>> ExtractEnums( CompilationUnitSyntax root )
   {
      var enums = new Dictionary<string, List<string>>();
      foreach( var enumDecl in root.DescendantNodes().OfType<EnumDeclarationSyntax>() )
      {
         var values = enumDecl.Members
            .Select( enumMember => enumMember.Identifier.Text )
            .Where( value => value != "None" && value != "Default" )
            .ToList();
         if( values.Count > 0 )
            enums[enumDecl.Identifier.Text] = values;
      }
      return enums;
   }

   /// <summary>Formats the collected enums as "EnumName(Member1,Member2)" entries, pipe-separated.</summary>
   private static string ExtractEnumValues( Dictionary<string, List<string>> enums ) =>
      string.Join( " | ", enums.Select( enumEntry => $"{enumEntry.Key}({string.Join( ",", enumEntry.Value )})" ) );

   /// <summary>Pipe-separated, distinct, alphabetically ordered list of the file's using directives.</summary>
   private static string ExtractUsings( CompilationUnitSyntax root ) =>
      string.Join( " | ", root.Usings.Select( usingDirective => usingDirective.Name?.ToString() ?? "" ).Where( name => name != "" ).Distinct().OrderBy( name => name ) );

   /// <summary>Space-separated distinct names of every attribute applied anywhere in the file.</summary>
   private static string ExtractAttributes( CompilationUnitSyntax root ) =>
      string.Join( " ", root.DescendantNodes().OfType<AttributeSyntax>()
         .Select( attribute => attribute.Name.ToString() ).Distinct() );

   /// <summary>Pipe-separated names of the file's <c>#region</c> directives (the "#region" prefix stripped).</summary>
   private static string ExtractRegions( CompilationUnitSyntax root ) =>
      string.Join( " | ", root.DescendantTrivia()
         .Where( trivia => trivia.IsKind( SyntaxKind.RegionDirectiveTrivia ) )
         .Select( trivia => trivia.ToString().Replace( "#region", "" ).Trim() )
         .Where( regionText => regionText.Length > 0 ) );

   /// <summary>Space-separated distinct names of the file's <c>const</c> fields (each declarator counted).</summary>
   private static string ExtractConstants( CompilationUnitSyntax root ) =>
      string.Join( " ", root.DescendantNodes().OfType<FieldDeclarationSyntax>()
         .Where( fieldDeclaration => fieldDeclaration.Modifiers.Any( SyntaxKind.ConstKeyword ) )
         .SelectMany( fieldDeclaration => fieldDeclaration.Declaration.Variables.Select( variable => variable.Identifier.Text ) ).Distinct() );

   /// <summary>Space-separated distinct names of the file's <c>static</c> (but not <c>const</c>) fields.</summary>
   private static string ExtractStaticFields( CompilationUnitSyntax root ) =>
      string.Join( " ", root.DescendantNodes().OfType<FieldDeclarationSyntax>()
         .Where( fieldDeclaration => fieldDeclaration.Modifiers.Any( SyntaxKind.StaticKeyword ) && !fieldDeclaration.Modifiers.Any( SyntaxKind.ConstKeyword ) )
         .SelectMany( fieldDeclaration => fieldDeclaration.Declaration.Variables.Select( variable => variable.Identifier.Text ) ).Distinct() );

   /// <summary>Pipe-separated distinct <c>Dictionary&lt;,&gt;</c> constructions (full generic text) used in the file.</summary>
   private static string ExtractDictionaries( CompilationUnitSyntax root ) =>
      string.Join( " | ", root.DescendantNodes().OfType<GenericNameSyntax>()
         .Where( genericName => genericName.Identifier.Text == "Dictionary" )
         .Select( genericName => genericName.ToString() ).Distinct() );

   /// <summary>Space-separated distinct identifiers of every generic type referenced in the file.</summary>
   private static string ExtractGenericTypes( CompilationUnitSyntax root ) =>
      string.Join( " ", root.DescendantNodes().OfType<GenericNameSyntax>()
         .Select( genericName => genericName.Identifier.Text ).Distinct() );

   /// <summary>
   /// Space-separated distinct event names, combining both forms: explicit event properties
   /// (<c>EventDeclarationSyntax</c>) and field-like events (<c>EventFieldDeclarationSyntax</c>).
   /// </summary>
   private static string ExtractEvents( CompilationUnitSyntax root )
   {
      var named = root.DescendantNodes().OfType<EventDeclarationSyntax>().Select( eventDecl => eventDecl.Identifier.Text );
      var fields = root.DescendantNodes().OfType<EventFieldDeclarationSyntax>()
         .SelectMany( eventField => eventField.Declaration.Variables.Select( variable => variable.Identifier.Text ) );
      return string.Join( " ", named.Concat( fields ).Distinct() );
   }

   /// <summary>Space-separated distinct names of every delegate type declared in the file.</summary>
   private static string ExtractDelegates( CompilationUnitSyntax root ) =>
      string.Join( " ", root.DescendantNodes().OfType<DelegateDeclarationSyntax>()
         .Select( delegateDecl => delegateDecl.Identifier.Text ).Distinct() );

   /// <summary>Space-separated SQL keywords (from <see cref="SqlKeywords"/>) that appear anywhere in the raw text, case-insensitively.</summary>
   private static string ExtractSqlOperations( string content ) =>
      string.Join( " ", SqlKeywords.Where( keyword => content.IndexOf( keyword, StringComparison.OrdinalIgnoreCase ) >= 0 ) );

   /// <summary>
   /// Builds the file's referenced-type graph: the sorted set of simple type names it names anywhere.
   /// This is the extractor's most valuable generic signal, so it walks every place a type can appear:
   /// object creations, variable and parameter declarations, method return types, base types, casts,
   /// typeof expressions, and generic type arguments. It also captures the leftmost identifier of a
   /// member-access chain (e.g. static helper or extension-class calls); an uppercase-first filter is
   /// applied there to skip locals and parameters while keeping type-like names.
   /// </summary>
   private static string ExtractReferencedTypes( CompilationUnitSyntax root )
   {
      var names = new HashSet<string>( StringComparer.Ordinal );

      void AddType( TypeSyntax? type )
      {
         if( type is null ) return;
         foreach( var name in SimpleTypeNames( type ) ) names.Add( name );
      }

      foreach( var objectCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>() ) AddType( objectCreation.Type );
      foreach( var variableDeclaration in root.DescendantNodes().OfType<VariableDeclarationSyntax>() ) AddType( variableDeclaration.Type );
      foreach( var parameter in root.DescendantNodes().OfType<ParameterSyntax>() ) AddType( parameter.Type );
      foreach( var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>() ) AddType( method.ReturnType );
      foreach( var baseType in root.DescendantNodes().OfType<BaseTypeSyntax>() ) AddType( baseType.Type );
      foreach( var castExpression in root.DescendantNodes().OfType<CastExpressionSyntax>() ) AddType( castExpression.Type );
      foreach( var typeOfExpression in root.DescendantNodes().OfType<TypeOfExpressionSyntax>() ) AddType( typeOfExpression.Type );
      foreach( var genericName in root.DescendantNodes().OfType<GenericNameSyntax>() )
      {
         names.Add( genericName.Identifier.Text );
         foreach( var arg in genericName.TypeArgumentList.Arguments ) AddType( arg );
      }
      // Leftmost identifier of a member-access chain: static helper / extension-class calls.
      // The uppercase filter skips locals and params.
      foreach( var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>() )
         if( memberAccess.Expression is IdentifierNameSyntax id && id.Identifier.Text is { Length: > 0 } identifierText && char.IsUpper( identifierText[0] ) )
            names.Add( identifierText );

      return string.Join( " ", names.OrderBy( name => name, StringComparer.Ordinal ) );
   }

   /// <summary>
   /// Recursively flattens a <see cref="TypeSyntax"/> into its simple (unqualified) type names.
   /// Handles the type shapes Roslyn can produce: plain identifiers, qualified and alias-qualified
   /// names (only the rightmost part is kept), generics (the open name plus every type argument),
   /// nullable types, and array types. Anything else yields nothing.
   /// </summary>
   private static IEnumerable<string> SimpleTypeNames( TypeSyntax type )
   {
      switch( type )
      {
         case IdentifierNameSyntax id:
            yield return id.Identifier.Text;
            break;
         case QualifiedNameSyntax qualifiedName:
            foreach( var name in SimpleTypeNames( qualifiedName.Right ) ) yield return name;
            break;
         case AliasQualifiedNameSyntax aliasQualifiedName:
            foreach( var name in SimpleTypeNames( aliasQualifiedName.Name ) ) yield return name;
            break;
         case GenericNameSyntax genericName:
            yield return genericName.Identifier.Text;
            foreach( var arg in genericName.TypeArgumentList.Arguments )
               foreach( var name in SimpleTypeNames( arg ) ) yield return name;
            break;
         case NullableTypeSyntax nullableType:
            foreach( var name in SimpleTypeNames( nullableType.ElementType ) ) yield return name;
            break;
         case ArrayTypeSyntax arrayType:
            foreach( var name in SimpleTypeNames( arrayType.ElementType ) ) yield return name;
            break;
      }
   }

   #endregion
}
