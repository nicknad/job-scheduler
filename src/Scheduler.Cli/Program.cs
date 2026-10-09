return args switch
{
    ["help"] or ["--help"] or ["-?"] => Usage(exitCode: 0),
    ["plugin", "list"] => ListPlugins(),
    ["plugin", "install", string path] => NotImplemented($"plugin install {path}"),
    ["plugin", "validate", string id, string version] => NotImplemented($"plugin validate {id} {version}"),
    ["plugin", "activate", string id, string version] => NotImplemented($"plugin activate {id} {version}"),
    ["plugin", "deactivate", string id] => NotImplemented($"plugin deactivate {id}"),
    ["plugin", "rollback", string id, string version] => NotImplemented($"plugin rollback {id} {version}"),
    ["plugin", "remove", string id] => NotImplemented($"plugin remove {id}"),
    ["job", "list"] => NotImplemented("job list"),
    ["job", "run", string id] => NotImplemented($"job run {id}"),
    _ => Usage(exitCode: 1),
};

static int ListPlugins()
{
    Console.WriteLine("No plugins installed (management API wiring pending).");
    return 0;
}

static int NotImplemented(string command)
{
    Console.Error.WriteLine($"'{command}' is not implemented yet; it will call the management API.");
    return 2;
}

static int Usage(int exitCode)
{
    Console.WriteLine("""
        Usage: scheduler <command> [arguments]

        Commands:
          plugin install <package-path>
          plugin validate <id> <version>
          plugin activate <id> <version>
          plugin deactivate <id>
          plugin rollback <id> <version>
          plugin remove <id>
          plugin list
          job list
          job run <id>
        """);
    return exitCode;
}
