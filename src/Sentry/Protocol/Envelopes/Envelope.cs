using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sentry.Extensibility;
using Sentry.Infrastructure;
using Sentry.Internal;
using Sentry.Internal.Extensions;

namespace Sentry.Protocol.Envelopes
{
    /// <summary>
    /// Envelope.
    /// </summary>
    public sealed class Envelope : ISerializable, IDisposable
    {
        /// <summary>
        /// Header associated with the envelope.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Header { get; }

        /// <summary>
        /// Envelope items.
        /// </summary>
        public IReadOnlyList<EnvelopeItem> Items { get; }

        /// <summary>
        /// Initializes an instance of <see cref="Envelope"/>.
        /// </summary>
        public Envelope(IReadOnlyDictionary<string, object?> header, IReadOnlyList<EnvelopeItem> items)
        {
            Header = header;
            Items = items;
        }

        /// <summary>
        /// Attempts to extract the value of "event_id" header if it's present.
        /// </summary>
        public SentryId? TryGetEventId() =>
            Header.TryGetValue("event_id", out var value) &&
            value is string valueString &&
            Guid.TryParse(valueString, out var guid)
                ? new SentryId(guid)
                : null;

        private async Task SerializeHeaderAsync(
            Stream stream,
            IDiagnosticLogger? logger,
            ISystemClock clock,
            CancellationToken cancellationToken)
        {
            // Append the sent_at header, except when writing to disk
            var headerItems = !stream.IsFileStream()
                ? Header.Append("sent_at", clock.GetUtcNow())
                : Header;

            var writer = new Utf8JsonWriter(stream);

#if NET461 || NETSTANDARD2_0
            await using (writer)
#else
            await using (writer.ConfigureAwait(false))
#endif
            {
                writer.WriteDictionaryValue(headerItems, logger);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void SerializeHeader(Stream stream, IDiagnosticLogger? logger, ISystemClock clock)
        {
            // Append the sent_at header, except when writing to disk
            var headerItems = !stream.IsFileStream()
                ? Header.Append("sent_at", clock.GetUtcNow())
                : Header;

            using var writer = new Utf8JsonWriter(stream);
            writer.WriteDictionaryValue(headerItems, logger);
            writer.Flush();
        }

        /// <inheritdoc />
        public Task SerializeAsync(
            Stream stream,
            IDiagnosticLogger? logger,
            CancellationToken cancellationToken = default) =>
            SerializeAsync(stream, logger, SystemClock.Clock, cancellationToken);

        internal async Task SerializeAsync(
            Stream stream,
            IDiagnosticLogger? logger,
            ISystemClock clock,
            CancellationToken cancellationToken = default)
        {
            // Header
            await SerializeHeaderAsync(stream, logger, clock, cancellationToken).ConfigureAwait(false);
            await stream.WriteNewlineAsync(cancellationToken).ConfigureAwait(false);

            // Items
            foreach (var item in Items)
            {
                await item.SerializeAsync(stream, logger, cancellationToken).ConfigureAwait(false);
                await stream.WriteNewlineAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public void Serialize(Stream stream, IDiagnosticLogger? logger) =>
            Serialize(stream, logger, SystemClock.Clock);

        internal void Serialize(Stream stream, IDiagnosticLogger? logger, ISystemClock clock)
        {
            // Header
            SerializeHeader(stream, logger, clock);
            stream.WriteNewline();

            // Items
            foreach (var item in Items)
            {
                item.Serialize(stream, logger);
                stream.WriteNewline();
            }
        }

        /// <inheritdoc />
        public void Dispose() => Items.DisposeAll();

        // limited SDK information (no packages)
        private static readonly IReadOnlyDictionary<string, string?> SdkHeader =
            new Dictionary<string, string?>(2, StringComparer.Ordinal)
            {
                ["name"] = SdkVersion.Instance.Name,
                ["version"] = SdkVersion.Instance.Version
            }.AsReadOnly();

        private static readonly IReadOnlyDictionary<string, object?> DefaultHeader =
            new Dictionary<string, object?>(1, StringComparer.Ordinal)
            {
                ["sdk"] = SdkHeader
            }.AsReadOnly();

        private static Dictionary<string, object?> CreateHeader(SentryId eventId, int extraCapacity = 0) =>
            new(2 + extraCapacity, StringComparer.Ordinal)
            {
                ["sdk"] = SdkHeader,
                ["event_id"] = eventId.ToString()
            };

        /// <summary>
        /// Creates an envelope that contains a single event.
        /// </summary>
        public static Envelope FromEvent(
            SentryEvent @event,
            IDiagnosticLogger? logger = null,
            IReadOnlyCollection<Attachment>? attachments = null,
            SessionUpdate? sessionUpdate = null)
        {
            var header = CreateHeader(@event.EventId);

            var items = new List<EnvelopeItem>
            {
                EnvelopeItem.FromEvent(@event)
            };

            if (attachments is not null)
            {
                foreach (var attachment in attachments)
                {
                    try
                    {
                        // We pull the stream out here so we can length check
                        // to avoid adding an invalid attachment
                        var stream = attachment.Content.GetStream();
                        if (stream.TryGetLength() != 0)
                        {
                            items.Add(EnvelopeItem.FromAttachment(attachment, stream));
                        }
                        else
                        {
                            // We would normally dispose the stream when we dispose the envelope item
                            // But in this case, we need to explicitly dispose here or we will be leaving
                            // the stream open indefinitely.
                            stream.Dispose();

                            logger?.LogWarning("Did not add '{0}' to envelope because the stream was empty.",
                                attachment.FileName);
                        }
                    }
                    catch (Exception exception)
                    {
                        logger?.LogError("Failed to add attachment: {0}.", exception, attachment.FileName);
                    }
                }
            }

            if (sessionUpdate is not null)
            {
                items.Add(EnvelopeItem.FromSession(sessionUpdate));
            }

            return new Envelope(header, items);
        }

        /// <summary>
        /// Creates an envelope that contains a single user feedback.
        /// </summary>
        public static Envelope FromUserFeedback(UserFeedback sentryUserFeedback)
        {
            var header = CreateHeader(sentryUserFeedback.EventId);

            var items = new[]
            {
                EnvelopeItem.FromUserFeedback(sentryUserFeedback)
            };

            return new Envelope(header, items);
        }

        /// <summary>
        /// Creates an envelope that contains a single transaction.
        /// </summary>
        public static Envelope FromTransaction(Transaction transaction)
        {
            Dictionary<string, object?> header;
            if (transaction.DynamicSamplingContext is { } dsc)
            {
                header = CreateHeader(transaction.EventId, extraCapacity: 1);
                header["trace"] = dsc.Items;
            }
            else
            {
                header = CreateHeader(transaction.EventId);
            }

            var items = new[]
            {
                EnvelopeItem.FromTransaction(transaction)
            };

            return new Envelope(header, items);
        }

        /// <summary>
        /// Creates an envelope that contains a session update.
        /// </summary>
        public static Envelope FromSession(SessionUpdate sessionUpdate)
        {
            var header = DefaultHeader;

            var items = new[]
            {
                EnvelopeItem.FromSession(sessionUpdate)
            };

            return new Envelope(header, items);
        }

        /// <summary>
        /// Creates an envelope that contains a client report.
        /// </summary>
        internal static Envelope FromClientReport(ClientReport clientReport)
        {
            var header = DefaultHeader;

            var items = new[]
            {
                EnvelopeItem.FromClientReport(clientReport)
            };

            return new Envelope(header, items);
        }

        private static async Task<IReadOnlyDictionary<string, object?>> DeserializeHeaderAsync(
            Stream stream,
            CancellationToken cancellationToken = default)
        {
            var buffer = await stream.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            var header =
                Json.Parse(buffer, JsonExtensions.GetDictionaryOrNull)
                ?? throw new InvalidOperationException("Envelope header is malformed.");

            // The sent_at header should not be included in the result
            header.Remove("sent_at");

            return header;
        }

        /// <summary>
        /// Deserializes envelope from stream.
        /// </summary>
        public static async Task<Envelope> DeserializeAsync(
            Stream stream,
            CancellationToken cancellationToken = default)
        {
            var header = await DeserializeHeaderAsync(stream, cancellationToken).ConfigureAwait(false);

            var items = new List<EnvelopeItem>();
            while (stream.Position < stream.Length)
            {
                var item = await EnvelopeItem.DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
                items.Add(item);
            }

            return new Envelope(header, items);
        }

        /// <summary>
        /// Creates a new <see cref="Envelope"/> starting from the current one and appends the <paramref name="item"/> given.
        /// </summary>
        /// <param name="item">The <see cref="EnvelopeItem"/> to append.</param>
        /// <returns>A new <see cref="Envelope"/> with the same headers and items, including the new <paramref name="item"/>.</returns>
        internal Envelope WithItem(EnvelopeItem item)
        {
            var items = Items.ToList();
            items.Add(item);
            return new Envelope(Header, items);
        }
    }
}