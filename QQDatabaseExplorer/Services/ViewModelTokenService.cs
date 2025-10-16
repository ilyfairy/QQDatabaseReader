
using System.Collections.Generic;
using Avalonia.Controls;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Services;

public class ViewModelTokenService
{
    public Dictionary<ViewModelToken, Control> Tokens { get; } = new();

    public void AutoRegister(ViewModelToken token, Control owner)
    {
        Tokens.Add(token, owner);
        owner.Unloaded += (_, _) => Unregister(token);
    }

    public void Register(ViewModelToken token, Control owner)
    {
        Tokens.Add(token, owner);
    }

    public void Unregister(ViewModelToken token)
    {
        Tokens.Remove(token);
    }
}
