using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AzureDevOpsForager.Core.Services.Utilities
{
   /// <summary>
   /// A single AES-encrypted store (secrets.enc, kept beside the binary) holding all of the app's named
   /// secrets as an encrypted JSON dictionary — currently the Groq API key and the Hugging Face token.
   /// Reads prefer the matching environment variable (GROQ_API_KEY, HF_TOKEN, ...) and fall back to the
   /// encrypted file, so a deployment can supply a secret either way. Writes merge one named secret in
   /// without disturbing the others. Encryption is delegated to <see cref="SecretBox"/>; a missing or
   /// unreadable file degrades to "no secret" rather than throwing, matching the app's fail-soft posture.
   /// This replaces the earlier single-secret groqapikey.enc (still read as a fallback for the Groq key).
   /// </summary>
   public static class SecretStore
   {
      #region Data Members

      /// <summary>File name of the consolidated encrypted secrets store, kept beside the executable.</summary>
      private const string SecretsFileName = "secrets.enc";

      /// <summary>Legacy single-secret file, read only as a fallback for GROQ_API_KEY (pre-consolidation).</summary>
      private const string LegacyGroqFileName = "groqapikey.enc";

      #endregion

      #region Public Methods

      /// <summary>
      /// Resolves a named secret. The environment variable of the same name wins when set; otherwise the
      /// value comes from the encrypted secrets.enc. As a migration convenience, GROQ_API_KEY also falls
      /// back to the legacy groqapikey.enc when secrets.enc has no such entry. Returns null when nothing
      /// is found, so callers can leave the dependent feature unconfigured and degrade gracefully.
      /// </summary>
      /// <param name="name">The secret's name, matching its environment-variable name (e.g. HF_TOKEN).</param>
      public static string Get( string name )
      {
         var fromEnvironment = Environment.GetEnvironmentVariable( name );
         if( !string.IsNullOrWhiteSpace( fromEnvironment ) )
            return fromEnvironment.Trim();

         var secrets = ReadAll();
         if( secrets.TryGetValue( name, out var value ) && !string.IsNullOrWhiteSpace( value ) )
            return value;

         if( name == "GROQ_API_KEY" )
            return ReadLegacyGroqKey();

         return null;
      }

      /// <summary>
      /// Stores or updates one named secret in secrets.enc, preserving every other secret already present:
      /// the full dictionary is read, merged, re-encrypted, and rewritten. IO failures propagate on purpose
      /// — this backs the one-shot `--set-secret` setup command, where a failed write must be visible.
      /// </summary>
      /// <param name="name">The secret's name (e.g. GROQ_API_KEY, HF_TOKEN).</param>
      /// <param name="value">The clear secret value to protect.</param>
      public static void Set( string name, string value )
      {
         var secrets = ReadAll();
         secrets[name] = value ?? "";
         var json = JsonConvert.SerializeObject( secrets );
         File.WriteAllText( SecretsPath, SecretBox.Encrypt( json ) );
      }

      #endregion

      #region Private Methods

      /// <summary>Absolute path to secrets.enc, next to the running binary.</summary>
      private static string SecretsPath => Path.Combine( AppDomain.CurrentDomain.BaseDirectory, SecretsFileName );

      /// <summary>
      /// Decrypts and parses secrets.enc into a name/value dictionary. A missing file, a decryption failure,
      /// or malformed JSON all degrade to an empty dictionary so a bad store never crashes startup.
      /// </summary>
      private static Dictionary<string, string> ReadAll()
      {
         try
         {
            if( !File.Exists( SecretsPath ) )
               return new Dictionary<string, string>();

            var decrypted = SecretBox.Decrypt( File.ReadAllText( SecretsPath ) );
            if( string.IsNullOrWhiteSpace( decrypted ) )
               return new Dictionary<string, string>();

            return JsonConvert.DeserializeObject<Dictionary<string, string>>( decrypted )
                   ?? new Dictionary<string, string>();
         }
         catch
         {
            return new Dictionary<string, string>();
         }
      }

      /// <summary>Reads the pre-consolidation groqapikey.enc (a single encrypted secret), or null if absent/bad.</summary>
      private static string ReadLegacyGroqKey()
      {
         try
         {
            var legacyPath = Path.Combine( AppDomain.CurrentDomain.BaseDirectory, LegacyGroqFileName );
            if( File.Exists( legacyPath ) )
               return SecretBox.Decrypt( File.ReadAllText( legacyPath ) );
         }
         catch
         {
            // Intentionally silent: a missing or unreadable legacy key just means "no secret here".
         }

         return null;
      }

      #endregion
   }
}
