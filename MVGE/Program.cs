using MVGE;

namespace GloriousTribesApp
{

    class Program
    {
        static void Main(string[] args)
        {
            using (GameManager game = new GameManager())
            {
                game.Run();
            }
        }
    }

}