using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClassIsland.Automation.Core.Models;

public enum ChatRole
{
    User,
    Assistant,
    System
}

public class ChatMessageItem : INotifyPropertyChanged
{
    private string _content = "";
    private bool _isThinking = false;
    private string _timeString = "";

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public ChatRole Role { get; set; } = ChatRole.User;

    public string SenderName => Role switch
    {
        ChatRole.User => "我",
        ChatRole.Assistant => "AI 助手",
        _ => "系统"
    };

    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsSystem => Role == ChatRole.System;

    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                OnPropertyChanged();
            }
        }
    }

    public string TimeString
    {
        get => _timeString;
        set
        {
            if (_timeString != value)
            {
                _timeString = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsThinking
    {
        get => _isThinking;
        set
        {
            if (_isThinking != value)
            {
                _isThinking = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<string> ToolBadges { get; } = new();

    public bool HasToolBadges => ToolBadges.Count > 0;

    public void AddToolBadge(string badge)
    {
        ToolBadges.Add(badge);
        OnPropertyChanged(nameof(HasToolBadges));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
