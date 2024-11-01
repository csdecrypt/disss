namespace Common
{
    public class MatchReponseItemDto
    {
        public MatchReponseItemDto(int score, string keyword)
        {
            this.Score = score;
            this.Keyword = keyword;
        }
        public int Score { get; set; }
        public string Keyword { get; set; }
    }
}
