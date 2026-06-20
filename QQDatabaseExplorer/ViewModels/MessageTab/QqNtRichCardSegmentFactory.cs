using System;
using System.Collections.Generic;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class QqNtRichCardSegmentFactory
{
    public static AvaQQMessageSegment? CreateRichCardSegment(
        QQForwardedMessage item,
        QQMessageSegment segment,
        Func<IReadOnlyList<QQForwardedMessage>, ForwardedMessageCard> createForwardedMessageCard)
    {
        if (TryCreateNestedForwardedMessageSegment(item, segment, createForwardedMessageCard) is { } nestedForwardedSegment)
            return nestedForwardedSegment;

        return CreateRichCardSegment(segment);
    }

    public static AvaQQMessageSegment? CreateRichCardSegment(QQMessageSegment segment)
    {
        if (SharedContactCardParser.TryParse(segment.AppJson, out var sharedContact) &&
            sharedContact is not null)
        {
            return AvaQQMessageSegment.CreateSharedContact(sharedContact);
        }

        if (MiniAppCardParser.TryParse(segment.AppJson, out var miniApp) &&
            miniApp is not null)
        {
            return AvaQQMessageSegment.CreateMiniApp(miniApp);
        }

        if (ForwardedMessageCardParser.TryParse(
                segment.AppJson,
                segment.AppResid,
                segment.AppUniseq,
                segment.Xml,
                segment.XmlResid,
                segment.XmlFileName,
                out var forwardedMessage) &&
            forwardedMessage is not null)
        {
            return AvaQQMessageSegment.CreateForwardedMessage(forwardedMessage);
        }

        return null;
    }

    private static AvaQQMessageSegment? TryCreateNestedForwardedMessageSegment(
        QQForwardedMessage item,
        QQMessageSegment segment,
        Func<IReadOnlyList<QQForwardedMessage>, ForwardedMessageCard> createForwardedMessageCard)
    {
        if (item.NestedForwardedMessages.Count == 0 ||
            SharedContactCardParser.TryParse(segment.AppJson, out _) ||
            MiniAppCardParser.TryParse(segment.AppJson, out _) ||
            ForwardedMessageCardParser.TryParse(
                segment.AppJson,
                segment.AppResid,
                segment.AppUniseq,
                segment.Xml,
                segment.XmlResid,
                segment.XmlFileName,
                out _))
        {
            return null;
        }

        if (segment.Type is not (MessageSegmentType.RichMedia or MessageSegmentType.App or MessageSegmentType.Xml) &&
            item.MessageType != MessageType.Forwarded &&
            !string.Equals(segment.GetDisplayText(), "[聊天记录]", StringComparison.Ordinal))
        {
            return null;
        }

        return AvaQQMessageSegment.CreateForwardedMessage(createForwardedMessageCard(item.NestedForwardedMessages));
    }
}
