using System.IO;

namespace Lavender.App;

public sealed record ExternalOpenRequest(string FilePath, int Line = 1, int Column = 1, string? ProjectPath = null)
{
    public static ExternalOpenRequest? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        string? file = null;
        string? project = null;
        int line = 1;
        int column = 1;
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (++i >= args.Length)
            {
                throw new ArgumentException($"Missing value for {option}.");
            }
            string value = args[i];

            switch (option)
            {
                case "--file": file = value; break;
                case "--project": project = value; break;
                case "--line": line = ParsePosition(value); break;
                case "--column": column = ParsePosition(value); break;
                default: throw new ArgumentException($"Unknown argument: {option}");
            }
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            throw new ArgumentException("Specify --file to open a source location.");
        }

        if(project is not null)
        {
            project = Path.GetFullPath(project);
        }
        file = Path.GetFullPath(file, project ?? Environment.CurrentDirectory);

        if (!File.Exists(file))
        {
            throw new FileNotFoundException("The requested source file does not exist.", file);
        }

        if (project is not null && !Directory.Exists(project))
        {
            throw new DirectoryNotFoundException(project);
        }

        return new(file, line, column, project);
    }

    private static int ParsePosition(string value)
    {
        int.TryParse(value, out int position);
        if(position <= 0)
        {
            throw new ArgumentException("Line nad column must be positive integers.");
        }
        return position;

    }
}
