extern alias HexGame;

using HexGame::Game.Shared.Domain;

namespace Dingler.Server
{

    public sealed class SessionContext
    {
        public Guid SessionId { get; init; }
        public DateTime CreationTime { get; init; }
        public bool IsAuthenticated { get; set; }
        public string? UserName { get; set; }
        public string? AuthToken { get; set; }
        public Dictionary<ulong, deck_bits> Decks { get; }
        public ulong AccountId { get; set; }
        public ulong ProfileId { get; set; }
        public ulong CurrentTournamentId { get; set; }
        public int CurrentMessageCount { get; set; }
        public DinglerEncoder Encoder { get; }

        private readonly object _afterResponseGate = new();
        private readonly Queue<object> _afterResponseMessages = new();
        
        public event Func<object, CancellationToken, Task>? SendMessageAsync;
        public event Func<object, bool>? TrySendMessage;

        public event Action? Disconnected;

        public SessionContext(Guid sessionId)
        {
            CurrentMessageCount = 1;
            SessionId = sessionId;
            CreationTime = DateTime.UtcNow;
            Decks = new Dictionary<ulong, deck_bits>();
            Encoder = new DinglerEncoder();
        }

        public void NotifyDisconnected()
        {
            try
            {
                Disconnected?.Invoke();
            }
            catch
            {
                // Swallow; disconnect notifications must not break teardown.
            }
        }

        public async Task SendMessageToClientAsync(object message, CancellationToken token)
        {
            try
            {
                if (SendMessageAsync is not null)
                    await SendMessageAsync.Invoke(message, token);
            }
            catch
            {
                // Swallow
            }
        }

        public bool TrySendMessageToClient(object message)
        {
            try
            {
                if (TrySendMessage is not null)
                    return TrySendMessage.Invoke(message);
            }
            catch
            {
                // swallow
            }
            
            return false;
        }

        /// <summary>
        /// Queue a server-initiated message to be written immediately after the
        /// response to the request currently being handled. This preserves the
        /// HConnect ordering used by services that reply and then push a notify.
        /// </summary>
        public void QueueMessageAfterResponse(object message)
        {
            lock (_afterResponseGate)
                _afterResponseMessages.Enqueue(message);
        }

        internal List<object> DrainMessagesAfterResponse()
        {
            lock (_afterResponseGate)
            {
                if (_afterResponseMessages.Count == 0)
                    return [];

                var messages = new List<object>(_afterResponseMessages.Count);
                while (_afterResponseMessages.Count > 0)
                    messages.Add(_afterResponseMessages.Dequeue());
                return messages;
            }
        }
    }
}
