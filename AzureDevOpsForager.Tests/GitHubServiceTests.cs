using AzureDevOpsForager.Core.Services.Integration;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Covers <see cref="GitHubService.ParseRepoUrl"/> — the pure URL-parsing surface of the
/// GitHub client. Exercises every accepted input shape: full HTTPS URLs, the bare
/// "owner/repo" shorthand, the ".git" suffix, SCP-style SSH remotes, trailing slashes,
/// and empty/null input.
/// </summary>
public class GitHubServiceTests
{
   [Theory]
   // Full HTTPS URL.
   [InlineData( "https://github.com/dotnet-architecture/eShopOnWeb", "dotnet-architecture", "eShopOnWeb" )]
   // Bare owner/repo shorthand (no host).
   [InlineData( "owner/repo", "owner", "repo" )]
   // The ".git" suffix is stripped.
   [InlineData( "https://github.com/owner/repo.git", "owner", "repo" )]
   // SCP-style SSH remote.
   [InlineData( "git@github.com:owner/repo", "owner", "repo" )]
   // Trailing slash is tolerated.
   [InlineData( "https://github.com/owner/repo/", "owner", "repo" )]
   // http:// scheme is also accepted.
   [InlineData( "http://github.com/owner/repo", "owner", "repo" )]
   public void ParseRepoUrl_ValidInputs_ReturnsOwnerAndRepo( string url, string expectedOwner, string expectedRepo )
   {
      var ( owner, repo ) = GitHubService.ParseRepoUrl( url );

      Assert.Equal( expectedOwner, owner );
      Assert.Equal( expectedRepo, repo );
   }

   [Theory]
   [InlineData( "" )]
   [InlineData( null )]
   [InlineData( "   " )]
   public void ParseRepoUrl_EmptyOrNull_ReturnsEmptyTuple( string? url )
   {
      var ( owner, repo ) = GitHubService.ParseRepoUrl( url! );

      Assert.Equal( "", owner );
      Assert.Equal( "", repo );
   }

   [Fact]
   public void ParseRepoUrl_DotGitSuffix_IsStrippedCaseInsensitively()
   {
      // The suffix strip is OrdinalIgnoreCase, so an uppercase ".GIT" is removed too.
      var ( owner, repo ) = GitHubService.ParseRepoUrl( "https://github.com/owner/repo.GIT" );

      Assert.Equal( "owner", owner );
      Assert.Equal( "repo", repo );
   }
}
