namespace HyperionPlayer
{
    class VKFriend
    {
        private string Name { get; }
        public long Id { get; }

        public VKFriend(string name, long id)
        {
            Name = name;
            Id = id;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    class VKAudio
    {
        public string Name { get; }
        public string Url { get; }

        public int Duration { get; }

        public VKAudio(string name, string url, int duration)
        {
            Name = name;
            Url = url;
            Duration = duration;
        }

        public override string ToString()
        {
            return Name;
        }

        public static string SecondToString(int duration)
        {
            return duration/60 + ":" + ((duration - duration/60*60) < 10 ? "0" : "") + (duration - duration / 60 * 60);
        }
    }
}