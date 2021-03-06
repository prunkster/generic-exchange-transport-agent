﻿namespace NeosIT.Exchange.GenericExchangeTransportAgent.Plugins.DkimSigningHandler.Impl
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Signs MIME messages according to the DKIM standard.
    /// </summary>
    public class DefaultDkimSigner : IDkimSigner
    {
        /// <summary>
        /// The sentinel for a header separator.
        /// </summary>
        private static readonly byte[] HeaderSeparatorSentinel = Encoding.ASCII.GetBytes("\r\n\r\n");

        /// <summary>
        /// The RSA crypto service provider.
        /// </summary>
        private RSACryptoServiceProvider cryptoProvider;

        /// <summary>
        /// A value indicating whether or not the instance of the signer has been disposed.
        /// </summary>
        private bool disposed;

        /// <summary>
        /// The domain that has the TXT record of the public key.
        /// </summary>
        private string domain;

        /// <summary>
        /// The headers that should be a part of the DKIM signature, if present in the message.
        /// </summary>
        private HashSet<string> eligibleHeaders;

        /// <summary>
        /// The hash algorithm that is to be employed.
        /// </summary>
        private HashAlgorithm hashAlgorithm;

        /// <summary>
        /// The DKIM code for the hash algorithm that is to be employed.
        /// </summary>
        private string hashAlgorithmDkimCode;

        /// <summary>
        /// The crypto provider code for the hash algorithm that is to be employed.
        /// </summary>
        private string hashAlgorithmCryptoCode;

        /// <summary>
        /// The selector that denotes which public key to use.
        /// </summary>
        private string selector;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultDkimSigner"/> class.
        /// </summary>
        /// <param name="signatureKind">The signature kind to use.</param>
        /// <param name="headersToSign">The headers to be signed. If null, only the From header will be signed.</param>
        /// <param name="selector">The domain containing the public key TXT record.</param>
        /// <param name="domain">The DNS selector.</param>
        /// <param name="encodedKey">The PEM-encoded key.</param>
        public DefaultDkimSigner(
            DkimAlgorithmKind signatureKind,
            IEnumerable<string> headersToSign,
            string selector,
            string domain,
            string encodedKey)
        {
            if (encodedKey == null ||
                encodedKey.Trim().Length == 0)
            {
                throw new ArgumentNullException("encodedKey");
            }

            if (selector == null ||
                selector.Trim().Length == 0)
            {
                throw new ArgumentNullException("selector");
            }

            if (domain == null ||
                domain.Trim().Length == 0)
            {
                throw new ArgumentNullException("domain");
            }

            this.cryptoProvider = CryptHelper.GetProviderFromPemEncodedRsaPrivateKey(encodedKey);
            this.domain = domain;
            this.selector = selector;

            switch (signatureKind)
            {
                case DkimAlgorithmKind.RsaSha1:
                    this.hashAlgorithm = new SHA1CryptoServiceProvider();
                    this.hashAlgorithmCryptoCode = "SHA1";
                    this.hashAlgorithmDkimCode = "rsa-sha1";
                    break;
                case DkimAlgorithmKind.RsaSha256:
                    this.hashAlgorithm = new SHA256CryptoServiceProvider();
                    this.hashAlgorithmCryptoCode = "SHA256";
                    this.hashAlgorithmDkimCode = "rsa-sha256";
                    break;
                default:
                    throw new ArgumentOutOfRangeException("signatureKind");
            }

            this.eligibleHeaders = new HashSet<string>();

            if (headersToSign != null)
            {
                foreach (var headerToSign in headersToSign)
                {
                    this.eligibleHeaders.Add(headerToSign.Trim());
                }
            }

            // The From header must always be signed according to the 
            // DKIM specification.
            this.eligibleHeaders.Add("From");
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="DefaultDkimSigner"/> class.
        /// </summary>
        ~DefaultDkimSigner()
        {
            this.Dispose(false);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, 
        /// releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Returns a value indicating whether or not the unsigned MIME message in the
        /// given stream can be signed. In this case, we iterate until we see the From:
        /// header, and then we only sign it if our domain matches the domain of the From:
        /// address.
        /// </summary>
        /// <param name="inputStream">The input stream.</param>
        /// <returns>The output stream.</returns>
        public bool CanSign(Stream inputStream)
        {
            bool canSign;
            string line;
            StreamReader reader;

            if (this.disposed)
            {
                throw new ObjectDisposedException("DomainKeysSigner");
            }

            if (inputStream == null)
            {
                throw new ArgumentNullException("inputStream");
            }

            canSign = false;
            reader = new StreamReader(inputStream);

            inputStream.Seek(0, SeekOrigin.Begin);

            line = reader.ReadLine();
            while (line != null)
            {
                string header;
                string[] headerParts;

                // We've reached the end of the headers (headers are
                // separated from the body by a blank line).
                if (line.Length == 0)
                {
                    break;
                }

                // Read a line. Because a header can be continued onto
                // subsequent lines, we have to keep reading lines until we
                // run into the end-of-headers marker (an empty line) or another
                // line that doesn't begin with a whitespace character.
                header = line + "\r\n";
                line = reader.ReadLine();
                while (!string.IsNullOrEmpty(line) &&
                    (line.StartsWith("\t", StringComparison.Ordinal) ||
                    line.StartsWith(" ", StringComparison.Ordinal)))
                {
                    header += line + "\r\n";
                    line = reader.ReadLine();
                }

                // Extract the name of the header. Then store the full header
                // in the dictionary. We do this because DKIM mandates that we
                // only sign the LAST instance of any header that occurs.
                headerParts = header.Split(new char[] { ':' }, 2);
                if (headerParts.Length == 2)
                {
                    string headerName;

                    headerName = headerParts[0];

                    if (headerName.Equals("From", StringComparison.OrdinalIgnoreCase))
                    {
                        // We don't break here because we want to read the bottom-most
                        // instance of the From: header (there should be only one, but
                        // if there are multiple, it's the last one that matters).
                        canSign = header
                            .ToUpperInvariant()
                            .Contains("@" + this.domain.ToUpperInvariant());
                    }
                }
            }

            inputStream.Seek(0, SeekOrigin.Begin);

            return canSign;
        }

        /// <summary>
        /// Writes a signed version of the unsigned MIME message in the input stream
        /// to the output stream.
        /// </summary>
        /// <param name="inputStream">The input stream.</param>
        /// <param name="outputStream">The output stream.</param>
        public void Sign(Stream inputStream, Stream outputStream)
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException("DomainKeysSigner");
            }

            if (inputStream == null)
            {
                throw new ArgumentNullException("inputStream");
            }

            if (outputStream == null)
            {
                throw new ArgumentNullException("outputStream");
            }

            var bodyHash = this.GetBodyHash(inputStream);
            var unsignedDkimHeader = this.GetUnsignedDkimHeader(bodyHash);
            var canonicalizedHeaders = this.GetCanonicalizedHeaders(inputStream);
            var signedDkimHeader = this.GetSignedDkimHeader(unsignedDkimHeader, canonicalizedHeaders);

            WriteSignedMimeMessage(inputStream, outputStream, signedDkimHeader);
        }

        /// <summary>
        /// Tests to see if the candidate sequence exists at the given position in the stream.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="candidate">The candidate we're looking for.</param>
        /// <returns>Whether or not the candidate exists at the current position in the stream.</returns>
        private static bool IsMatch(Stream stream, byte[] candidate)
        {
            if (candidate.Length > (stream.Length - stream.Position))
            {
                return false;
            }

            for (int i = 0; i < candidate.Length; ++i)
            {
                if (stream.ReadByte() != candidate[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Writes the message containing the signed DKIM-Signature header to the output stream.
        /// </summary>
        /// <param name="input">The stream containing the original MIME message.</param>
        /// <param name="output">The stream containing the output MIME message.</param>
        /// <param name="signedDkimHeader">The signed DKIM-Signature header.</param>
        private static void WriteSignedMimeMessage(Stream input, Stream output, string signedDkimHeader)
        {
            int bytesRead;
            byte[] headerBuffer;
            byte[] streamBuffer;

            input.Seek(0, SeekOrigin.Begin);

            headerBuffer = Encoding.ASCII.GetBytes(signedDkimHeader);
            output.Write(headerBuffer, 0, headerBuffer.Length);

            streamBuffer = new byte[1024];
            while ((bytesRead = input.Read(streamBuffer, 0, streamBuffer.Length)) > 0)
            {
                output.Write(streamBuffer, 0, bytesRead);
            }
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources;
        /// <c>false</c> to release only unmanaged resources.</param>
        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (this.cryptoProvider != null)
                {
                    this.cryptoProvider.Clear();
                    this.cryptoProvider = null;
                }

                if (this.hashAlgorithm != null)
                {
                    this.hashAlgorithm.Clear();
                    this.hashAlgorithm = null;
                }
            }

            this.disposed = true;
        }

        /// <summary>
        /// Computes the hash of the body of the MIME message (represented by the given stream)
        /// and returned as a base64-encoded string. The stream position is reset back to the start
        /// of the stream.
        /// </summary>
        /// <param name="stream">The stream that represents the MIME message.</param>
        /// <returns>The base64-encoded hash of the body.</returns>
        private string GetBodyHash(Stream stream)
        {
            byte[] bodyBytes;
            string bodyText;
            string hashText;
            long index;

            index = -1;

            stream.Seek(0, SeekOrigin.Begin);

            while (stream.Position < stream.Length)
            {
                if (IsMatch(stream, HeaderSeparatorSentinel))
                {
                    index = stream.Position;
                    break;
                }
            }

            if (index < 0)
            {
                throw new ArgumentException(
                    "The stream did not have a MIME body.",
                    "stream");
            }

            // We have to ignore all empty lines at the end of the message body.
            // This means we have to read the whole body and fix up the end of the
            // body if necessary.
            bodyText = new StreamReader(stream).ReadToEnd();
            bodyText = Regex.Replace(bodyText, "(\r?\n)*$", string.Empty);
            bodyText += "\r\n";
            bodyBytes = Encoding.ASCII.GetBytes(bodyText);

            hashText = Convert.ToBase64String(this.hashAlgorithm.ComputeHash(bodyBytes));

            stream.Seek(0, SeekOrigin.Begin);

            return hashText;
        }

        /// <summary>
        /// Runs through the MIME stream and sucks out the headers that should be part
        /// of the DKIM signature. The headers here have their whitespace preserved and
        /// all terminate with CRLF. The stream position is returned to the start of the
        /// stream.
        /// </summary>
        /// <param name="stream">The stream containing the MIME message.</param>
        /// <returns>An enumerable of the headers that should be a part of the signature.</returns>
        private IEnumerable<string> GetCanonicalizedHeaders(Stream stream)
        {
            Dictionary<string, string> headerNameToLineMap;
            string line;
            StreamReader reader;

            stream.Seek(0, SeekOrigin.Begin);

            headerNameToLineMap = new Dictionary<string, string>();
            reader = new StreamReader(stream);

            line = reader.ReadLine();
            while (line != null)
            {
                string header;
                string[] headerParts;

                // We've reached the end of the headers (headers are
                // separated from the body by a blank line).
                if (line.Length == 0)
                {
                    break;
                }

                // Read a line. Because a header can be continued onto
                // subsequent lines, we have to keep reading lines until we
                // run into the end-of-headers marker (an empty line) or another
                // line that doesn't begin with a whitespace character.
                header = line + "\r\n";
                line = reader.ReadLine();
                while (!string.IsNullOrEmpty(line) &&
                    (line.StartsWith("\t", StringComparison.Ordinal) ||
                    line.StartsWith(" ", StringComparison.Ordinal)))
                {
                    header += line + "\r\n";
                    line = reader.ReadLine();
                }

                // Extract the name of the header. Then store the full header
                // in the dictionary. We do this because DKIM mandates that we
                // only sign the LAST instance of any header that occurs.
                headerParts = header.Split(new char[] { ':' }, 2);
                if (headerParts.Length == 2)
                {
                    string headerName;

                    headerName = headerParts[0];

                    // We only want to sign the header if we were told to sign it!
                    if (this.eligibleHeaders.Contains(headerName, StringComparer.OrdinalIgnoreCase))
                    {
                        headerNameToLineMap[headerName] = header;
                    }
                }
            }

            stream.Seek(0, SeekOrigin.Begin);

            return headerNameToLineMap.Values.OrderBy(x => x, StringComparer.Ordinal);
        }

        /// <summary>
        /// Gets the version of the DKIM-Signature header with the signature appended, along with
        /// the CRLF.
        /// </summary>
        /// <param name="unsignedDkimHeader">The unsigned DKIM header, to use as a template.</param>
        /// <param name="canonicalizedHeaders">The headers to be included as part of the signature.</param>
        /// <returns>The signed DKIM-Signature header.</returns>
        private string GetSignedDkimHeader(
            string unsignedDkimHeader,
            IEnumerable<string> canonicalizedHeaders)
        {
            byte[] signatureBytes;
            string signatureText;
            StringBuilder signedDkimHeader;

            using (var stream = new MemoryStream())
            {
                using (var writer = new StreamWriter(stream))
                {
                    foreach (var canonicalizedHeader in canonicalizedHeaders)
                    {
                        writer.Write(canonicalizedHeader);
                    }

                    writer.Write(unsignedDkimHeader);
                    writer.Flush();

                    stream.Seek(0, SeekOrigin.Begin);

                    // Why not pass this.hashAlgorithm here, since we already have it? If we're supporting
                    // Exchange 2007, then we're stuck on CLR 2.0. The SHA-256 functionality was added in
                    // .NET 3.5 SP1, but it was done in such a way that the switch statement used internally
                    // by the Crypto .NET classes won't recognize the new SHA256CryptoServiceProvider type.
                    // So, we have to use the string method instead. More details available at
                    // http://blogs.msdn.com/b/shawnfa/archive/2008/08/25/using-rsacryptoserviceprovider-for-rsa-sha256-signatures.aspx
                    signatureBytes = this.cryptoProvider.SignData(stream, this.hashAlgorithmCryptoCode);
                }
            }

            signatureText = Convert.ToBase64String(signatureBytes);
            signedDkimHeader = new StringBuilder(unsignedDkimHeader.Substring(0, unsignedDkimHeader.Length - 1));

            signedDkimHeader.Append(signatureText);
            signedDkimHeader.Append(";\r\n");

            return signedDkimHeader.ToString();
        }

        /// <summary>
        /// Builds an unsigned DKIM-Signature header. Note that the returned
        /// header will NOT have a CRLF at the end.
        /// </summary>
        /// <param name="bodyHash">The hash of the body.</param>
        /// <returns>The unsigned DKIM-Signature header.</returns>
        private string GetUnsignedDkimHeader(string bodyHash)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "DKIM-Signature: v=1; a={0}; s={1}; d={2}; c=simple/simple; q=dns/txt; h={3}; bh={4}; b=;",
                this.hashAlgorithmDkimCode,
                this.selector,
                this.domain,
                string.Join(" : ", this.eligibleHeaders.OrderBy(x => x, StringComparer.Ordinal).ToArray()),
                bodyHash);
        }
    }
}
