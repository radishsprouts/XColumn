namespace XColumn.Models
{
    /// <summary>
    /// カラムのレート制限ステータス。
    /// </summary>
    public enum RateLimitStatus
    {
        /// <summary>通常状態。</summary>
        Normal,

        /// <summary>残数僅少の注意状態。</summary>
        Caution,

        /// <summary>API制限または先回り停止による更新停止中状態。</summary>
        Stopped
    }
}
