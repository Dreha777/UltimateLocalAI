using System.Text.Json;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class ChatRepository
{
    private sealed class ChatStore
    {
        public long NextMessageId { get; set; } = 1;
        public List<ChatSession> Chats { get; set; } = [];
        public List<ChatMessage> Messages { get; set; } = [];
    }

    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private ChatStore _store;

    public ChatRepository()
    {
        AppPaths.EnsureDirectories();
        _store = Load();
        TryDailyBackup();
    }

    private ChatStore Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ChatsFile)) return new ChatStore();
            var json = File.ReadAllText(AppPaths.ChatsFile);
            return JsonSerializer.Deserialize<ChatStore>(json) ?? new ChatStore();
        }
        catch (Exception ex)
        {
            LogService.Warn("Не удалось прочитать chats.json: " + ex.Message);
            var rescue = Path.Combine(AppPaths.BackupsDir, $"chats_corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            try { File.Copy(AppPaths.ChatsFile, rescue, true); } catch { }
            return new ChatStore();
        }
    }

    private void SaveLocked()
    {
        var tmp = AppPaths.ChatsFile + ".tmp";
        var json = JsonSerializer.Serialize(_store, _json);
        File.WriteAllText(tmp, json);
        if (File.Exists(AppPaths.ChatsFile))
            File.Replace(tmp, AppPaths.ChatsFile, null, true);
        else
            File.Move(tmp, AppPaths.ChatsFile);
    }

    private void TryDailyBackup()
    {
        try
        {
            if (!File.Exists(AppPaths.ChatsFile)) return;
            var backup = Path.Combine(AppPaths.BackupsDir, $"chats_{DateTime.Now:yyyyMMdd}.json");
            if (!File.Exists(backup)) File.Copy(AppPaths.ChatsFile, backup);
        }
        catch (Exception ex) { LogService.Warn("Backup chats.json: " + ex.Message); }
    }

    public List<ChatSession> GetChats()
    {
        lock (_gate)
            return _store.Chats.OrderByDescending(x => x.UpdatedAt).Select(CloneChat).ToList();
    }

    public ChatSession CreateChat(string modelPath)
    {
        lock (_gate)
        {
            var chat = new ChatSession { ModelPath = modelPath ?? "" };
            _store.Chats.Add(chat);
            SaveLocked();
            return CloneChat(chat);
        }
    }

    public void SaveMessage(ChatMessage message)
    {
        lock (_gate)
        {
            message.Id = _store.NextMessageId++;
            _store.Messages.Add(CloneMessage(message));
            var chat = _store.Chats.FirstOrDefault(x => x.Id == message.ChatId);
            if (chat is not null) chat.UpdatedAt = DateTime.Now;
            SaveLocked();
        }
    }

    public List<ChatMessage> GetMessages(string chatId)
    {
        lock (_gate)
            return _store.Messages.Where(x => x.ChatId == chatId).OrderBy(x => x.Id).Select(CloneMessage).ToList();
    }

    public void RenameChat(string id, string title)
    {
        lock (_gate)
        {
            var chat = _store.Chats.FirstOrDefault(x => x.Id == id);
            if (chat is null) return;
            chat.Title = string.IsNullOrWhiteSpace(title) ? "Новый чат" : title.Trim();
            chat.UpdatedAt = DateTime.Now;
            SaveLocked();
        }
    }

    public void DeleteChat(string id)
    {
        lock (_gate)
        {
            _store.Messages.RemoveAll(x => x.ChatId == id);
            _store.Chats.RemoveAll(x => x.Id == id);
            SaveLocked();
        }
    }

    public void UpdateModelPath(string chatId, string modelPath)
    {
        lock (_gate)
        {
            var chat = _store.Chats.FirstOrDefault(x => x.Id == chatId);
            if (chat is null) return;
            chat.ModelPath = modelPath ?? "";
            chat.UpdatedAt = DateTime.Now;
            SaveLocked();
        }
    }

    public string DatabasePath => AppPaths.ChatsFile;

    private static ChatSession CloneChat(ChatSession x) => new()
    {
        Id = x.Id, Title = x.Title, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, ModelPath = x.ModelPath
    };

    private static ChatMessage CloneMessage(ChatMessage x) => new()
    {
        Id = x.Id, ChatId = x.ChatId, Role = x.Role, Content = x.Content, ContextText = x.ContextText,
        CreatedAt = x.CreatedAt,
        Attachments = x.Attachments.Select(a => new AttachmentInfo
        {
            FileName = a.FileName, FullPath = a.FullPath, SizeBytes = a.SizeBytes, ExtractedText = a.ExtractedText
        }).ToList()
    };
}
