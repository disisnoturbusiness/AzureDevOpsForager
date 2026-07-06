using System;
using System.Security.Cryptography;
using System.Text;

namespace AzureDevOpsForager.Core.Services.Utilities;
/// <summary>
/// Lightweight AES-256 encrypt/decrypt helper for a single secret held at rest, for example the Groq
/// API key persisted in groqapikey.enc. The symmetric key is derived from an app-embedded constant,
/// so the application can decrypt the secret on launch with no user prompt and on any operating system
/// (Windows or the Linux server). This is deliberately obfuscation-grade rather than a secrets vault:
/// its only job is to keep the key out of plaintext on disk. The GROQ_API_KEY environment variable
/// remains the recommended source and, whenever it is set, always takes precedence over the file.
/// </summary>
public static class SecretBox
{
   #region Data Members

   /// <summary>
   /// App-embedded passphrase fed into the key-derivation function. This is intentionally not a real
   /// secret (it ships in the binary); it exists only to make the on-disk ciphertext non-trivial to
   /// read, not to withstand a determined attacker who has the assembly.
   /// </summary>
   private const string Passphrase = "AzureDevOpsForager::secretbox::v1";

   /// <summary>
   /// Number of PBKDF2 iterations used when stretching the passphrase into an AES key. Higher counts
   /// slow brute-force attempts; 100k is a reasonable cost for a launch-time derivation.
   /// </summary>
   private const int KeyDerivationIterations = 100_000;

   /// <summary>
   /// Size in bytes of the derived symmetric key. 32 bytes yields AES-256.
   /// </summary>
   private const int AesKeySizeBytes = 32;

   /// <summary>
   /// Size in bytes of the AES initialization vector. AES uses a 16-byte (128-bit) block/IV. The IV is
   /// generated per-encrypt and prepended to the ciphertext so decryption can recover it.
   /// </summary>
   private const int AesInitializationVectorSizeBytes = 16;

   /// <summary>
   /// Fixed salt combined with the passphrase during key derivation. A constant salt is acceptable here
   /// because both sides (encrypt and decrypt) must derive the identical key without any stored state;
   /// per-secret salting would defeat the "decrypt silently on launch" goal.
   /// </summary>
   private static readonly byte[] Salt = Encoding.UTF8.GetBytes( "adf-secretbox-salt-v1" );

   #endregion

   #region Public Methods

   /// <summary>
   /// Encrypts a plaintext string and returns base64( IV + ciphertext ). A fresh random IV is generated
   /// for every call and prepended to the ciphertext so that <see cref="Decrypt"/> can recover it. An
   /// empty or null input yields an empty string so callers can round-trip "no secret" cleanly.
   /// </summary>
   /// <param name="plaintext">The clear secret to protect (e.g. an API key).</param>
   /// <returns>Base64 of (IV followed by ciphertext), or an empty string when the input is empty.</returns>
   public static string Encrypt( string plaintext )
   {
      if( string.IsNullOrEmpty( plaintext ) ) return "";

      using var aes = Aes.Create();
      aes.Key = DeriveKey();
      aes.GenerateIV();

      using var encryptor = aes.CreateEncryptor();
      var plaintextBytes = Encoding.UTF8.GetBytes( plaintext );
      var cipherBytes = encryptor.TransformFinalBlock( plaintextBytes, 0, plaintextBytes.Length );

      // Lay out the IV first, then the ciphertext, in one buffer so the pair travels together.
      var combined = new byte[aes.IV.Length + cipherBytes.Length];
      Buffer.BlockCopy( aes.IV, 0, combined, 0, aes.IV.Length );
      Buffer.BlockCopy( cipherBytes, 0, combined, aes.IV.Length, cipherBytes.Length );

      return Convert.ToBase64String( combined );
   }

   /// <summary>
   /// Decrypts a value previously produced by <see cref="Encrypt"/>, i.e. base64( IV + ciphertext ),
   /// back to its plaintext. Deliberately forgiving: any malformed input, wrong key, or corruption is
   /// swallowed and returns null so a bad on-disk secret degrades to "no secret" rather than crashing
   /// the app at startup.
   /// </summary>
   /// <param name="encoded">The base64 string emitted by <see cref="Encrypt"/>.</param>
   /// <returns>The recovered plaintext, or null if the input is empty or cannot be decrypted.</returns>
   public static string Decrypt( string encoded )
   {
      try
      {
         if( string.IsNullOrWhiteSpace( encoded ) ) return null;

         var combined = Convert.FromBase64String( encoded.Trim() );

         // Anything at or below the IV length carries no ciphertext, so it can't be a valid payload.
         if( combined.Length <= AesInitializationVectorSizeBytes ) return null;

         using var aes = Aes.Create();
         aes.Key = DeriveKey();
         aes.IV = ExtractInitializationVector( combined );

         using var decryptor = aes.CreateDecryptor();
         var cipherStart = AesInitializationVectorSizeBytes;
         var cipherLength = combined.Length - AesInitializationVectorSizeBytes;
         var plaintextBytes = decryptor.TransformFinalBlock( combined, cipherStart, cipherLength );

         return Encoding.UTF8.GetString( plaintextBytes );
      }
      catch { return null; }
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// Stretches the embedded passphrase and salt into a 32-byte AES-256 key via PBKDF2. Uses the
   /// netstandard2.0-safe constructor (which selects a SHA-1-based KDF), which is acceptable for
   /// obfuscation-at-rest where the passphrase itself ships in the binary.
   /// </summary>
   /// <returns>The derived 32-byte symmetric key.</returns>
   private static byte[] DeriveKey()
   {
      using var keyDerivation = new Rfc2898DeriveBytes( Passphrase, Salt, KeyDerivationIterations );
      return keyDerivation.GetBytes( AesKeySizeBytes );
   }

   /// <summary>
   /// Copies the leading IV bytes out of a combined (IV + ciphertext) buffer produced by
   /// <see cref="Encrypt"/>. Callers must have already verified the buffer is long enough to hold an IV.
   /// </summary>
   /// <param name="combined">The full base64-decoded payload (IV followed by ciphertext).</param>
   /// <returns>The 16-byte initialization vector.</returns>
   private static byte[] ExtractInitializationVector( byte[] combined )
   {
      var initializationVector = new byte[AesInitializationVectorSizeBytes];
      Buffer.BlockCopy( combined, 0, initializationVector, 0, AesInitializationVectorSizeBytes );
      return initializationVector;
   }

   #endregion
}
