// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NubeZero.Server.Services
{
    /// <summary>
    /// Provides AES‑GCM encryption and decryption utilities for end‑to‑end file protection.
    /// The secret key is derived from a password supplied by the client (which in turn is
    /// obtained securely from the server during the login flow). The server stores only the
    /// encrypted bytes; the clear text key never touches the disk.
    /// </summary>
    public class EncryptionService
    {
        private const int KeySize = 32; // 256‑bit key
        private const int NonceSize = 12; // Recommended size for AES‑GCM
        private const int TagSize = 16; // Authentication tag size

        /// <summary>
        /// Derives a 256‑bit key from a password using PBKDF2 with a per‑session random salt.
        /// The salt is stored alongside the ciphertext (first 16 bytes).
        /// </summary>
        public static byte[] DeriveKey(string password, byte[] salt)
        {
            using var kdf = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
            return kdf.GetBytes(KeySize);
        }

        /// <summary>
        /// Encrypts the supplied plaintext using AES‑GCM. Returns a byte array formatted as:
        /// [16‑byte salt][12‑byte nonce][ciphertext][16‑byte tag]
        /// </summary>
        public static async Task<byte[]> EncryptAsync(Stream plaintext, string password)
        {
            // Generate a random salt and nonce
            var salt = RandomNumberGenerator.GetBytes(16);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var key = DeriveKey(password, salt);

            using var aes = new AesGcm(key);
            using var ms = new MemoryStream();
            await plaintext.CopyToAsync(ms);
            var plainBytes = ms.ToArray();

            var cipher = new byte[plainBytes.Length];
            var tag = new byte[TagSize];
            aes.Encrypt(nonce, plainBytes, cipher, tag);

            // Concatenate salt, nonce, ciphertext and tag
            using var result = new MemoryStream();
            result.Write(salt, 0, salt.Length);
            result.Write(nonce, 0, nonce.Length);
            result.Write(cipher, 0, cipher.Length);
            result.Write(tag, 0, tag.Length);
            return result.ToArray();
        }

        /// <summary>
        /// Decrypts data that was encrypted with <see cref="EncryptAsync"/>.
        /// The input format must be: [salt][nonce][cipher][tag]
        /// </summary>
        public static async Task<byte[]> DecryptAsync(Stream ciphertextStream, string password)
        {
            using var ms = new MemoryStream();
            await ciphertextStream.CopyToAsync(ms);
            var all = ms.ToArray();

            // Extract components
            var offset = 0;
            var salt = new byte[16];
            Buffer.BlockCopy(all, offset, salt, 0, 16);
            offset += 16;

            var nonce = new byte[NonceSize];
            Buffer.BlockCopy(all, offset, nonce, 0, NonceSize);
            offset += NonceSize;

            var tag = new byte[TagSize];
            Buffer.BlockCopy(all, all.Length - TagSize, tag, 0, TagSize);
            var cipherLength = all.Length - offset - TagSize;
            var cipher = new byte[cipherLength];
            Buffer.BlockCopy(all, offset, cipher, 0, cipherLength);

            var key = DeriveKey(password, salt);
            var plain = new byte[cipherLength];
            using var aes = new AesGcm(key);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
    }
}
