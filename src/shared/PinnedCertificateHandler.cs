using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NubeZero.Shared
{
    public static class PinnedCertificateHandler
    {
        public static RemoteCertificateValidationCallback CreateCallback(string fingerprint)
        {
            string expectedFingerprint = Normalize(fingerprint);
            if (expectedFingerprint.Length != 64)
                throw new ArgumentException("La huella del certificado debe contener 64 dígitos hexadecimales SHA-256.", nameof(fingerprint));

            return (sender, certificate, chain, errors) =>
            {
                if (certificate == null)
                    return false;

                var certificate2 = certificate as X509Certificate2;
                bool ownsCertificate = certificate2 == null;
                certificate2 ??= new X509Certificate2(certificate);
                try
                {
                    DateTime now = DateTime.UtcNow;
                    if (now < certificate2.NotBefore.ToUniversalTime() || now > certificate2.NotAfter.ToUniversalTime())
                        return false;

                    byte[] actualFingerprint;
                    using (var sha256 = SHA256.Create())
                        actualFingerprint = sha256.ComputeHash(certificate2.GetRawCertData());

                    byte[] expectedBytes = FromHex(expectedFingerprint);
                    int difference = 0;
                    for (int i = 0; i < actualFingerprint.Length; i++)
                        difference |= actualFingerprint[i] ^ expectedBytes[i];

                    Array.Clear(actualFingerprint, 0, actualFingerprint.Length);
                    Array.Clear(expectedBytes, 0, expectedBytes.Length);
                    return difference == 0;
                }
                finally
                {
                    if (ownsCertificate)
                        certificate2.Dispose();
                }
            };
        }

        private static string Normalize(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
                return string.Empty;

            var result = new System.Text.StringBuilder(64);
            foreach (char value in fingerprint)
            {
                if (value == ':' || value == ' ' || value == '-')
                    continue;

                if (!Uri.IsHexDigit(value))
                    return string.Empty;

                result.Append(char.ToUpperInvariant(value));
            }

            return result.ToString();
        }

        private static byte[] FromHex(string value)
        {
            var result = new byte[value.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return result;
        }
    }
}
