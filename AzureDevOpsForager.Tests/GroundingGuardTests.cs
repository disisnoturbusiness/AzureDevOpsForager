using AzureDevOpsForager.Core.Services.Chat;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Covers <see cref="GroundingGuard"/> — the check that stops /chat asking the model a question no
/// retrieved code can answer.
///
/// This exists because of a specific, observed failure: once the relevance gate started correctly
/// returning zero results for an unanswerable question, /chat kept calling the model anyway with an empty
/// context, and the model produced a confident 3,655-character answer out of its own training data. The
/// response is presented to the user as an answer about their repository. An empty result list is honest;
/// a fluent answer with no source behind it is not.
/// </summary>
public class GroundingGuardTests
{
   [Fact]
   public void EmptyContext_HasNoGrounding()
   {
      Assert.False( GroundingGuard.HasGrounding( "" ) );
   }

   [Fact]
   public void NullContext_HasNoGrounding()
   {
      Assert.False( GroundingGuard.HasGrounding( null ) );
   }

   [Theory]
   [InlineData( " " )]
   [InlineData( "\n" )]
   [InlineData( "\r\n\r\n" )]
   [InlineData( "   \t  \n " )]
   public void WhitespaceOnlyContext_HasNoGrounding( string context )
   {
      // The context is built by appending "// File: x" headers and chunk bodies, so whitespace-only means
      // the loop never ran. Treating it as grounding would send the model a blank prompt — the exact case
      // this guard exists for — so the check must be IsNullOrWhiteSpace, not IsNullOrEmpty.
      Assert.False( GroundingGuard.HasGrounding( context ) );
   }

   [Fact]
   public void RetrievedCode_HasGrounding()
   {
      var context = "// File: src/ApplicationCore/Entities/BasketAggregate/Basket.cs\npublic class Basket { }\n";

      Assert.True( GroundingGuard.HasGrounding( context ) );
   }

   [Fact]
   public void NoGroundingAnswer_SaysTheIndexWasEmptyRatherThanRefusing()
   {
      // The wording is part of the contract: the user must be able to tell that retrieval came back empty,
      // not that the assistant declined or that their question was invalid. If this message is ever
      // rewritten into a bare "I don't know", the distinction the guard exists to make is lost.
      Assert.Contains( "indexed codebase", GroundingGuard.NoGroundingAnswer );
      Assert.Contains( "retrieved", GroundingGuard.NoGroundingAnswer );
      Assert.False( string.IsNullOrWhiteSpace( GroundingGuard.NoGroundingAnswer ) );
   }
}
