using MVGE;
using MVGE.Middleware;

namespace GloriousTribesApp
{

    class Program
    {
        static void Main(string[] args)
        {
            ArgumentsMiddleware.Parse(args);
            using (GameManager game = new GameManager())
            {
                game.Run();
            }
        }
    }

}