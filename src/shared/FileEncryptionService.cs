using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NubeZero.Shared
{
    public static class FileEncryptionService
    {
        private const int IvSize = 16;
        private const int TagSize = 32;
        private const int HeaderSize = 4 + IvSize + 8;
        private const int BufferSize = 81920;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NZF1");

        public static async Task<long> GetPlaintextLengthAsync(Stream encrypted)
        {
            if (encrypted == null) throw new ArgumentNullException(nameof(encrypted));
            if (!encrypted.CanSeek || !encrypted.CanRead)
                throw new ArgumentException("El archivo cifrado debe permitir lectura y búsqueda.", nameof(encrypted));

            long originalPosition = encrypted.Position;
            try
            {
                encrypted.Position = 0;
                byte[] header = new byte[HeaderSize];
                await ReadExactlyAsync(encrypted, header, 0, header.Length);
                ValidateHeader(header);

                long plaintextLength = ReadInt64LittleEndian(header, 4 + IvSize);
                long ciphertextLength = encrypted.Length - HeaderSize - TagSize;
                long expectedCiphertextLength = GetPaddedCiphertextLength(plaintextLength);

                if (ciphertextLength != expectedCiphertextLength)
                    throw new CryptographicException("La longitud del archivo cifrado no es válida.");

                return plaintextLength;
            }
            finally
            {
                encrypted.Position = originalPosition;
            }
        }

        public static long GetEncryptedLength(long plaintextLength)
        {
            if (plaintextLength < 0)
                throw new ArgumentOutOfRangeException(nameof(plaintextLength));
            return checked(HeaderSize + GetPaddedCiphertextLength(plaintextLength) + TagSize);
        }

        public static async Task EncryptAsync(Stream plaintext, Stream encrypted, byte[] masterKey, Action<long, long>? progress = null)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            if (encrypted == null) throw new ArgumentNullException(nameof(encrypted));
            if (!plaintext.CanRead || !plaintext.CanSeek || !encrypted.CanWrite)
                throw new ArgumentException("Los flujos deben permitir lectura, búsqueda y escritura.");
            ValidateMasterKey(masterKey);

            long plaintextLength = plaintext.Length - plaintext.Position;
            if (plaintextLength < 0)
                throw new ArgumentException("La posición del flujo supera su longitud.", nameof(plaintext));

            byte[]? encryptionKey = null;
            byte[]? authenticationKey = null;
            byte[] header = new byte[HeaderSize];
            byte[] iv = new byte[IvSize];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(iv);
            }

            Buffer.BlockCopy(Magic, 0, header, 0, Magic.Length);
            Buffer.BlockCopy(iv, 0, header, Magic.Length, iv.Length);
            WriteInt64LittleEndian(header, Magic.Length + IvSize, plaintextLength);

            try
            {
                encryptionKey = DeriveKey(masterKey, "NubeZero file encryption key v1");
                authenticationKey = DeriveKey(masterKey, "NubeZero file authentication key v1");

                using (var hmac = new HMACSHA256(authenticationKey))
                {
                    hmac.TransformBlock(header, 0, header.Length, header, 0);
                    await encrypted.WriteAsync(header, 0, header.Length);

                    using (var aes = Aes.Create())
                    {
                        aes.Key = encryptionKey;
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;

                        var hashingStream = new HmacWriteStream(encrypted, hmac, progress, (plaintextLength / IvSize + 1) * IvSize);
                        using (var cryptoStream = new CryptoStream(hashingStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
                        {
                            await plaintext.CopyToAsync(cryptoStream, BufferSize);
                            cryptoStream.FlushFinalBlock();
                        }
                    }

                    hmac.TransformFinalBlock(new byte[0], 0, 0);
                    byte[] tag = hmac.Hash;
                    await encrypted.WriteAsync(tag, 0, tag.Length);
                    Array.Clear(tag, 0, tag.Length);
                }
            }
            finally
            {
                Clear(encryptionKey, authenticationKey, iv, header);
            }
        }

        public static async Task DecryptAsync(Stream encrypted, Stream plaintext, byte[] masterKey)
        {
            if (encrypted == null) throw new ArgumentNullException(nameof(encrypted));
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            if (!encrypted.CanRead || !encrypted.CanSeek || !plaintext.CanWrite)
                throw new ArgumentException("El archivo cifrado debe permitir lectura y búsqueda, y el destino escritura.");
            ValidateMasterKey(masterKey);

            byte[]? encryptionKey = null;
            byte[]? authenticationKey = null;
            byte[] header = new byte[HeaderSize];
            byte[] expectedTag = new byte[TagSize];
            byte[] buffer = new byte[BufferSize];
            try
            {
                encryptionKey = DeriveKey(masterKey, "NubeZero file encryption key v1");
                authenticationKey = DeriveKey(masterKey, "NubeZero file authentication key v1");

                encrypted.Position = 0;
                await ReadExactlyAsync(encrypted, header, 0, header.Length);
                ValidateHeader(header);

                long plaintextLength = ReadInt64LittleEndian(header, 4 + IvSize);
                long ciphertextLength = encrypted.Length - HeaderSize - TagSize;
                long expectedCiphertextLength = GetPaddedCiphertextLength(plaintextLength);
                if (ciphertextLength != expectedCiphertextLength)
                    throw new CryptographicException("La longitud del archivo cifrado no es válida.");

                using (var hmac = new HMACSHA256(authenticationKey))
                {
                    hmac.TransformBlock(header, 0, header.Length, header, 0);
                    encrypted.Position = HeaderSize;
                    long remaining = ciphertextLength;
                    while (remaining > 0)
                    {
                        int count = (int)Math.Min(buffer.Length, remaining);
                        await ReadExactlyAsync(encrypted, buffer, 0, count);
                        hmac.TransformBlock(buffer, 0, count, buffer, 0);
                        remaining -= count;
                    }

                    await ReadExactlyAsync(encrypted, expectedTag, 0, expectedTag.Length);
                    hmac.TransformFinalBlock(new byte[0], 0, 0);
                    if (!FixedTimeEquals(hmac.Hash, expectedTag))
                        throw new CryptographicException("La verificación de integridad del archivo falló.");
                }

                encrypted.Position = HeaderSize;
                using (var aes = Aes.Create())
                {
                    aes.Key = encryptionKey;
                    byte[] iv = new byte[IvSize];
                    Buffer.BlockCopy(header, Magic.Length, iv, 0, iv.Length);
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using (var limitedStream = new LimitedReadStream(encrypted, ciphertextLength))
                    using (var cryptoStream = new CryptoStream(limitedStream, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    {
                        await cryptoStream.CopyToAsync(plaintext, BufferSize);
                    }
                    Array.Clear(iv, 0, iv.Length);
                }
            }
            finally
            {
                Clear(encryptionKey, authenticationKey, header, expectedTag, buffer);
            }
        }

        private static void ValidateMasterKey(byte[] masterKey)
        {
            if (masterKey == null || masterKey.Length != 32)
                throw new ArgumentException("La clave de cifrado debe contener exactamente 32 bytes.", nameof(masterKey));
        }

        private static long GetPaddedCiphertextLength(long plaintextLength)
        {
            if (plaintextLength < 0 || plaintextLength > long.MaxValue - IvSize)
                throw new CryptographicException("La longitud del archivo cifrado no es válida.");
            return checked((plaintextLength / IvSize + 1) * IvSize);
        }

        private static byte[] DeriveKey(byte[] masterKey, string purpose)
        {
            using (var hmac = new HMACSHA256(masterKey))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(purpose));
            }
        }

        private static void ValidateHeader(byte[] header)
        {
            for (int i = 0; i < Magic.Length; i++)
            {
                if (header[i] != Magic[i])
                    throw new CryptographicException("El formato del archivo cifrado no es compatible.");
            }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            int difference = 0;
            for (int i = 0; i < left.Length; i++)
                difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static long ReadInt64LittleEndian(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value |= (ulong)bytes[offset + i] << (8 * i);
            return unchecked((long)value);
        }

        private static void WriteInt64LittleEndian(byte[] bytes, int offset, long value)
        {
            ulong unsignedValue = unchecked((ulong)value);
            for (int i = 0; i < 8; i++)
                bytes[offset + i] = (byte)(unsignedValue >> (8 * i));
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = await stream.ReadAsync(buffer, offset, count);
                if (read == 0)
                    throw new CryptographicException("El archivo cifrado está truncado.");
                offset += read;
                count -= read;
            }
        }

        private static void Clear(params byte[]?[] arrays)
        {
            foreach (byte[]? array in arrays)
            {
                if (array != null)
                    Array.Clear(array, 0, array.Length);
            }
        }

        private sealed class HmacWriteStream : Stream
        {
            private readonly Stream _output;
            private readonly HMACSHA256 _hmac;
            private readonly Action<long, long>? _progress;
            private readonly long _total;
            private long _written;

            public HmacWriteStream(Stream output, HMACSHA256 hmac, Action<long, long>? progress, long total)
            {
                _output = output;
                _hmac = hmac;
                _progress = progress;
                _total = total;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() => _output.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                _hmac.TransformBlock(buffer, offset, count, buffer, offset);
                _output.Write(buffer, offset, count);
                ReportProgress(count);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
            {
                _hmac.TransformBlock(buffer, offset, count, buffer, offset);
                await _output.WriteAsync(buffer, offset, count, cancellationToken);
                ReportProgress(count);
            }

            private void ReportProgress(int count)
            {
                _written += count;
                _progress?.Invoke(_written, _total);
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
            }
        }

        private sealed class LimitedReadStream : Stream
        {
            private readonly Stream _source;
            private long _remaining;

            public LimitedReadStream(Stream source, long length)
            {
                _source = source;
                _remaining = length;
            }

            public override bool CanRead => _source.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _remaining;
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, (int)Math.Min(count, _remaining));
                _remaining -= read;
                return read;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
            {
                if (_remaining == 0)
                    return 0;
                int read = await _source.ReadAsync(buffer, offset, (int)Math.Min(count, _remaining), cancellationToken);
                _remaining -= read;
                return read;
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
            }
        }
    }
}
