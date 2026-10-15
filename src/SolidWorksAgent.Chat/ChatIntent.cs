namespace SolidWorksAgent.Chat;

public enum ChatIntent { Message, Execute, ExecuteWithOverwrite, Cancel }

public static class ChatIntents
{
    // Only the user's entire message can authorize a pending batch. Quoted text,
    // negation, questions and model output must never grant execution/overwrite.
    public static ChatIntent Parse(string text)
    {
        var value = text.Trim().TrimEnd('。', '！', '!').Trim();
        return value switch
        {
            "実行" or "実行して" or "実行してください" or "その内容で実行して" or "それでお願いします"
                or "進めて" or "進めてください" or "お願いします" or "はい" => ChatIntent.Execute,
            "上書きを許可して実行" or "上書きを許可して実行して" or "上書きを許可して実行してください" => ChatIntent.ExecuteWithOverwrite,
            "やめて" or "中止" or "中止して" or "キャンセル" or "取り消して" or "停止" or "停止して" => ChatIntent.Cancel,
            _ => ChatIntent.Message
        };
    }

    public static bool IsReadOnly(CadCommand command) => command.Command is
        "api search" or "api describe" or "info" or "documents list" or "sketch list" or "sketch inspect" or "part inspect" or "drawing inspect" or "assembly analyze";
}
