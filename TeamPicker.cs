using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Entities;
using Microsoft.Extensions.Logging;
using System;
using CounterStrikeSharp.API.Modules.Commands.Targeting;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using System.Text.Json.Serialization;
using MySqlConnector;
using System.Collections.Concurrent;
using System.Globalization;
using CounterStrikeSharp.API.Modules.Cvars;
using MenuManager;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Menu;

namespace TeamPicker;

public class TeamPickerConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")] 
    public override int Version { get; set; } = 3;

    [JsonPropertyName("DbHost")]
    public string DbHost { get; set; } = "";

    [JsonPropertyName("DbPort")]
    public string DbPort { get; set; } = "3306";
    
    [JsonPropertyName("DbUser")]
    public string DbUser { get; set; } = "";

    [JsonPropertyName("DbPassword")]
    public string DbPass { get; set; } = "";

    [JsonPropertyName("DbName")]
    public string DbName { get; set; } = "";

    [JsonPropertyName("MapPool")]
    public List<string> MapPool { get; set; } = new List<string> 
    { 
        "de_mirage", 
        "de_inferno", 
        "de_nuke", 
        "de_overpass", 
        "de_dust2", 
        "de_ancient", 
        "de_anubis" 
    };

    [JsonIgnore] 
    public string ConnectionString => $"Server={DbHost};Port={DbPort};Database={DbName};User ID={DbUser};Password={DbPass};";
}

public enum States
{
    Disabled,
    Active,
    ChoosingCaptains,
    CaptainsPicking,
    GettingPlayerLevel,
    LevelRandomizing,
    Randomizing,
    MapVeto,
    GettingReady
}

public enum Modes
{
    Captians,
    Level,
    Random
}

public enum PickOrder
{
    ABABABAB,
    ABBABABA,
    ABBAABBA
}

public class TeamPicker : BasePlugin, IPluginConfig<TeamPickerConfig>
{
    public override string ModuleName => "TeamPicker";
    public override string ModuleVersion => "1.0";
    public override string ModuleAuthor => "4NTEDEGUEM0N";
    public override string ModuleDescription => "Team Picker for PUGs (Random/Captains)";

    public override void Load(bool hotReload)
    {
        Task.Run(InitDatabase);
        _api = _pluginCapability.Get();
        if (_api == null)
            Logger.LogInformation("ERRO: MenuManager Core não foi encontrado!");
        
        Logger.LogInformation("TeamPicker loaded successfully!");
    }

    public bool DBConnected = false;
    public States currentState = States.Disabled;
    public Modes mode = Modes.Captians;
    public CCSPlayerController? Captain1 { get; set; }
    public CCSPlayerController? Captain2 { get; set; }
    public PickOrder pickOrder = PickOrder.ABABABAB;
    public int pickOrderIndex = 0;
    public bool x1 = false;
    public bool bots = false;
    public int CurrentPickTurn { get; set; } = 1; // 1 = Vez do Cap1, 2 = Vez do Cap2
    public ConcurrentDictionary<string, int> PlayersLevel = new ConcurrentDictionary<string, int>();
    public Dictionary<string, CsTeam> PlayersTeam = new Dictionary<string, CsTeam>();
    public List<CCSPlayerController> PlayersToPick = new List<CCSPlayerController>();
    private IMenuApi? _api;
    private readonly PluginCapability<IMenuApi?> _pluginCapability = new("menu:nfcore");

    public TeamPickerConfig Config { get; set; } = new TeamPickerConfig();
    public List<string> MapsRemaining { get; set; } = new List<string>();
    public void OnConfigParsed(TeamPickerConfig config)
    {
        this.Config = config;

        if (string.IsNullOrEmpty(config.DbHost) || string.IsNullOrEmpty(config.DbUser))
        {
            Logger.LogError("Configuração de Banco de Dados incompleta! Verifique o arquivo json.");
        }

        if (config.MapPool == null || config.MapPool.Count < 1)
        {
            config.MapPool = new List<string> { "de_mirage", "de_inferno", "de_nuke", "de_overpass", "de_dust2", "de_ancient", "de_anubis"};
            Logger.LogWarning("MapPool estava vazio na config! Usando padrão.");
        }

        Logger.LogInformation($"Config carregada com {config.MapPool.Count} mapas.");
    }

    private async Task InitDatabase()
    {
        try
        {
            string connString = Config.ConnectionString;
            using var conn = new MySqlConnection(connString);
            await conn.OpenAsync();
            
            string sql = @"
                CREATE TABLE IF NOT EXISTS `gc_levels` (
                  `steamid` VARCHAR(64) NOT NULL,
                  `level` VARCHAR(10) NOT NULL,
                  `updated_at` DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                  PRIMARY KEY (`steamid`)
                );";

            using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
            DBConnected = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TeamPicker SQL Error] Falha ao iniciar banco: {ex.Message}");
        }
    }

    /*
    [ConsoleCommand("css_rcon", "Server RCON")]
    [RequiresPermissions("@css/rcon")]
    public void RconCommand(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters))
            return;
        Server.ExecuteCommand(parameters);
    }
    */

    public HookResult OnPlayerTryJoinTeam(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;

        if (currentState == States.CaptainsPicking || x1)
        {
            Server.ExecuteCommand($"echo Troca cancelada: {player.PlayerName}");
            return HookResult.Stop;
        }

        return HookResult.Continue;
    }

    [ConsoleCommand("css_tp", "Activate TeamPicker Plugin")]
    public void TeamPickerCommand(CCSPlayerController? player, CommandInfo command)
    {
        bool disabledOrActive =  currentState == States.Disabled || currentState == States.Active;

        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters) && disabledOrActive)
            mode = Modes.Captians;
        else if ((parameters == "3"|| string.Equals(parameters, "random", StringComparison.OrdinalIgnoreCase)) && disabledOrActive)
        {
            mode = Modes.Random;
        }
        else if ((parameters == "1" || string.Equals(parameters, "captains", StringComparison.OrdinalIgnoreCase)) && disabledOrActive)
        {
            mode = Modes.Captians;
        }
        else if ((parameters == "2" || string.Equals(parameters, "level", StringComparison.OrdinalIgnoreCase)) && disabledOrActive)
        {
            mode = Modes.Level;
        }
        else if (string.Equals(parameters, "bots", StringComparison.OrdinalIgnoreCase) && (currentState != States.Disabled))
        {
            ToggleBots();
            return;
        }
        else if (string.Equals(parameters, "start", StringComparison.OrdinalIgnoreCase) && (currentState != States.Disabled))
        {
            Start();
            return;
        }
        else if (string.Equals(parameters, "restart", StringComparison.OrdinalIgnoreCase) && (currentState != States.Disabled))
        {
            ClearData();
            currentState = States.Active;
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Restarted");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Mode:{ChatColors.Red} {mode}{ChatColors.Default}");
            return;
        }
        else if (string.Equals(parameters, "disable", StringComparison.OrdinalIgnoreCase) && (currentState != States.Disabled))
        {
            ClearData();
            currentState = States.Disabled;
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Disabled");
            return;
        }
        else
            return;

        if (currentState == States.Disabled)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Activated");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Mode:{ChatColors.Red} {mode}{ChatColors.Default}");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !modes{ChatColors.Default} para ver os modos disponíveis.");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !tp start{ChatColors.Default} para confirmar.");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !help{ChatColors.Default} para ver os comandos disponíveis.");
            currentState = States.Active;
        }
        else
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Mode:{ChatColors.Red} {mode}{ChatColors.Default}");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !modes{ChatColors.Default} para ver os modos disponíveis.");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !tp start{ChatColors.Default} para confirmar.");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !help{ChatColors.Default} para ver os comandos disponíveis.");
        }
    }

    public void ToggleBots()
    {
        bots = bots == false;
        if (bots)
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Bots{ChatColors.Green} activated{ChatColors.Default}.");
        else
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Bots{ChatColors.Red} disabled{ChatColors.Default}.");
    }

    [ConsoleCommand("css_help", "Show commands")]
    public void HelpCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;

        if (currentState == States.Active)
        {
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Mode:{ChatColors.Red} {mode}{ChatColors.Default}");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !modes{ChatColors.Default} para ver os modos disponíveis.");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !tp start{ChatColors.Default} para confirmar.");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !help{ChatColors.Default} para ver os comandos disponíveis.");
        }
        else if (currentState == States.ChoosingCaptains)
        {
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 1: {ChatColors.Green} {Captain1?.PlayerName}{ChatColors.Default}  (CT)");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 2: {ChatColors.Green} {Captain2?.PlayerName}{ChatColors.Default}  (TR)");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Ordem de Picks: {ChatColors.Green} {pickOrder}{ChatColors.Default} ");
            if (_api == null)
            {
                player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !captain1 <nome>{ChatColors.Default} para trocar o capitão 1.");
                player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !captain2 <nome>{ChatColors.Default} para trocar o capitão 2.");
            }
            else
                player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !captains {ChatColors.Default} para trocar os capitães.");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !random{ChatColors.Default} randomizar os capitães.");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !pickorder{ChatColors.Default} para ver as ordens de pick disponíveis.");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !help{ChatColors.Default} para verificar a configuração atual.");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !tp start{ChatColors.Default} para confirmar.");
        }
        else if (currentState == States.Randomizing)
        {
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 1: {ChatColors.Blue} {Captain1?.PlayerName}{ChatColors.Default} (CT)");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 2: {ChatColors.Orange} {Captain2?.PlayerName}{ChatColors.Default} (TR)");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !random{ChatColors.Default} randomizar os times.");
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !tp start{ChatColors.Default} para confirmar.");
        }
    }

    [ConsoleCommand("css_modes", "Print TeamPicker Modes")]
    public void ModesCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;

        var index = 1;
        if (_api == null)
        {
            foreach (string modes in Enum.GetNames(typeof(Modes)))
            {
                player?.PrintToChat($" {ChatColors.Green} {index}{ChatColors.Default} -> {modes}");
                index++;
            }
            player?.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !tp <mode>{ChatColors.Default} para escolher!");
        }
        else
        {
            var menu = _api.GetMenu(" {Red}Escolha um Modo");
            foreach (string mode in Enum.GetNames(typeof(Modes)))
            {
                int currentIndex = index;
                menu.AddMenuOption($"{mode}", (p, option) => {Server.ExecuteCommand($"css_tp {currentIndex}");});
                index++;
            }

            menu.PostSelectAction = PostSelectAction.Close;
            menu.Open(player);
        }
    }

    public void Start()
        {
            switch(mode)
            {
                case Modes.Captians:
                    if (currentState == States.Active)
                    {
                        ClearData();
                        ChoosingCaptains();
                    }
                    else if (currentState == States.ChoosingCaptains)
                    {
                        if (!x1)
                            X1();
                    }
                    break;
                case Modes.Level:
                    if (currentState == States.Active)
                    {
                        if (DBConnected)
                        {
                            ClearData();
                            GettingPlayersLevel();
                        }
                        else
                        {
                            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Red} Falha ao conectar com o banco de dados.");
                        }
                    }
                    else if (currentState == States.GettingPlayerLevel)
                    {
                        LevelRandomTeamPicker();
                    }
                    else if (currentState == States.LevelRandomizing)
                    {
                        if (!x1)
                            X1();
                    }
                    break;
                case Modes.Random:
                    if (currentState == States.Active)
                    {
                        ClearData();
                        RandomTeamPicker();
                    }
                    else if (currentState == States.Randomizing)
                    {
                        if (!x1)
                            X1();
                    }
                    break;
            }
            return;
        }

    public void ChoosingCaptains()
    {
        currentState = States.ChoosingCaptains;

        List<CCSPlayerController>? players;
        if (bots)
            players = Utilities.GetPlayers().Where(p => !p.IsHLTV).ToList();
        else
            players = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV).ToList();

        if (players.Count < 2)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Jogadores insuficientes para iniciar capitães!");
            ClearData();
            currentState = States.Active;
            return;
        }

        Random rnd = new Random();
        var randomPlayers = players.OrderBy(x => rnd.Next()).ToList();

        Captain1 = randomPlayers[0];
        Captain2 = randomPlayers[1];

        ChangeTeamHandler(Captain1, CsTeam.CounterTerrorist);
        ChangeTeamHandler(Captain2, CsTeam.Terrorist);

        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 1: {ChatColors.Green} {Captain1?.PlayerName}{ChatColors.Default}  (CT)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 2: {ChatColors.Green} {Captain2?.PlayerName}{ChatColors.Default}  (TR)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Ordem de Picks: {ChatColors.Green} {pickOrder}{ChatColors.Default} ");
        if (_api == null)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !captain1 <nome>{ChatColors.Default} para trocar o capitão 1.");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !captain2 <nome>{ChatColors.Default} para trocar o capitão 2.");
        }
        else
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !captains {ChatColors.Default} para trocar os capitães.");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !random{ChatColors.Default} randomizar os capitães.");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !pickorder{ChatColors.Default} para ver as ordens de pick disponíveis.");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !help{ChatColors.Default} para verificar a configuração atual.");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !tp start{ChatColors.Default} para confirmar.");
    }

    [ConsoleCommand("css_captain1", "Choose Captain1")]
    public void Captain1Command(CCSPlayerController? player, CommandInfo command)
    {
        if (currentState != States.ChoosingCaptains && currentState != States.Randomizing && currentState != States.LevelRandomizing) return;

        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters))
            return;
        
        var players = Utilities.GetPlayers();
        var target = players.FirstOrDefault(p => p.PlayerName.Contains(parameters, StringComparison.OrdinalIgnoreCase));
        
        if (target != null && target != Captain2)
        {
            Captain1 = target;
            ChangeTeamHandler(Captain1, CsTeam.CounterTerrorist);
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Green} {target.PlayerName}{ChatColors.Default} é o Capitão 1");
        }
        else
        {
            if (player == null) return;
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Jogador não encontrado ou ele é o capitão 2.");
        }
    }

    [ConsoleCommand("css_captain2", "Choose Captain1")]
    public void Captain2Command(CCSPlayerController? player, CommandInfo command)
    {
        if (currentState != States.ChoosingCaptains && currentState != States.Randomizing && currentState != States.LevelRandomizing) return;

        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters))
            return;
        
        var players = Utilities.GetPlayers();
        var target = players.FirstOrDefault(p => p.PlayerName.Contains(parameters, StringComparison.OrdinalIgnoreCase));
        
        if (target != null && target != Captain1)
        {
            Captain2 = target;
            ChangeTeamHandler(Captain2, CsTeam.Terrorist);
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Green} {target.PlayerName}{ChatColors.Default} é o Capitão 2");
        }
        else
        {
            if (player == null) return;
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Jogador não encontrado ou ele é o capitão 1.");
        }
    }

    [ConsoleCommand("css_captains", "Print current captains")]
    public void CaptainsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (currentState != States.ChoosingCaptains) return;
        if (_api == null) return;

        var menu = _api.GetMenu(" {Red}Quer trocar qual capitão?");
        menu.AddMenuOption($"Capitão 1", (p, option) => {ChooseCaptain(player, "1", command);});
        menu.AddMenuOption($"Capitão 2", (p, option) => {ChooseCaptain(player, "2", command);});

        menu.PostSelectAction = PostSelectAction.Nothing;
        if (player != null)
            menu.Open(player);
    }

    public void ChooseCaptain(CCSPlayerController? player, string index, CommandInfo command)
    {
        if (_api == null) return;

        List<CCSPlayerController>? players;
        if (bots)
            players = Utilities.GetPlayers().Where(p => !p.IsHLTV && p != Captain1 && p != Captain2).ToList();
        else
            players = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV && p != Captain1 && p != Captain2).ToList();

        var menu = _api.GetMenu($" {{Red}}Escolha o Capitão {index}");
        foreach (var p in players)
        {
            var playerName = p.PlayerName;
            menu.AddMenuOption($"{playerName}", (p, option) => {
                Server.ExecuteCommand($"css_captain{index} {playerName}");
                CaptainsCommand(player, command);
            });
        }

        menu.PostSelectAction = PostSelectAction.Nothing;
        if (player != null)
            menu.Open(player);
    }

    public void RandomCaptains()
    {
        if (currentState != States.ChoosingCaptains) return;

        List<CCSPlayerController>? players;
        if (bots)
            players = Utilities.GetPlayers().Where(p => !p.IsHLTV).ToList();
        else
            players = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV).ToList();

        if (players.Count < 2)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Jogadores insuficientes para iniciar capitães!");
            ClearData();
            currentState = States.Active;
            return;
        }

        Random rnd = new Random();
        var randomPlayers = players.OrderBy(x => rnd.Next()).ToList();

        Captain1 = randomPlayers[0];
        Captain2 = randomPlayers[1];

        ChangeTeamHandler(Captain1, CsTeam.CounterTerrorist);
        ChangeTeamHandler(Captain2, CsTeam.Terrorist);

        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 1: {ChatColors.Blue} {Captain1?.PlayerName}{ChatColors.Default} (CT)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 2: {ChatColors.Orange} {Captain2?.PlayerName}{ChatColors.Default} (TR)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Ordem de Picks: {ChatColors.Green} {pickOrder}{ChatColors.Default} ");
    }

    [ConsoleCommand("css_pickorder", "Change Pick Order")]
    public void PickOrderCommand(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters))
        {
            var index = 1;
            if (_api == null)
            {
                foreach (string orders in Enum.GetNames(typeof(PickOrder)))
                {
                    player?.PrintToChat($" {ChatColors.Green} {index}{ChatColors.Default} -> {orders}");
                    index++;
                }
                player?.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !pickorder <numero>{ChatColors.Default} para escolher!");
                }
            else
            {
                var menu = _api.GetMenu(" {Red}Escolha a ordem de Pick");
                foreach (string order in Enum.GetNames(typeof(PickOrder)))
                {
                    int currentIndex = index;
                    menu.AddMenuOption($"{order}", (p, option) => {Server.ExecuteCommand($"css_pickorder {currentIndex}");});
                    index++;
                }

                menu.PostSelectAction = PostSelectAction.Close;
                if (player != null)
                    menu.Open(player);
            }
        }
        else
        {
            if (!int.TryParse(parameters, out int index)) return;
            if (index > Enum.GetValues(typeof(PickOrder)).Length || index < 1) return;

            pickOrder = Enum.GetValues<PickOrder>()[index-1];
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Ordem de Picks alterada para {ChatColors.Green} {pickOrder}{ChatColors.Default} .");
        }
    }

    private void IniciarContagemRegressiva(int segundos, Action func)
    {
        if (currentState == States.Disabled || currentState == States.Active) return;

        if (segundos > 0)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Red} {segundos}{ChatColors.Default}...");
            
            AddTimer(1.0f, () => IniciarContagemRegressiva(segundos - 1, func));
        }
        else
        {
            func.Invoke();
        }
    }

    public void X1()
    {
        x1 = true;
        
        List<CCSPlayerController>? specPlayers;
        if (bots)
            specPlayers = Utilities.GetPlayers().Where(p => !p.IsHLTV && p != Captain1 && p != Captain2).ToList();
        else
            specPlayers = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV && p != Captain1 && p != Captain2).ToList();

        foreach (var player in specPlayers)
        {
            ChangeTeamHandler(player, CsTeam.Spectator);
        }

        if (Captain1 != null && Captain2 != null)
        {
            Captain1.Respawn();
            Captain2.Respawn();
        }

        List<string> arenas = ["Bombsite A", "Meio", "Bomsite B"];
        Random rnd = new Random();
        var randomArenas = arenas.OrderBy(x => rnd.Next()).ToList();
        var selectedArena = randomArenas[0];
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} o X1 vai começar em 3 segundo!");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} O local da batalha é o{ChatColors.Red} {selectedArena}{ChatColors.Default} !");
        IniciarContagemRegressiva(3, IniciarX1);
    }

    public void IniciarX1()
    {
        Captain1?.Respawn();
        Captain2?.Respawn();

        Captain1?.RemoveWeapons();
        Captain1?.GiveNamedItem("weapon_deagle");
        Captain1?.GiveNamedItem("weapon_knife");

        Captain2?.RemoveWeapons();
        Captain2?.GiveNamedItem("weapon_deagle");
        Captain2?.GiveNamedItem("weapon_knife");

        RegisterEventHandler<EventPlayerDeath>(HandlerOnDeathEvent);
        AddCommandListener("jointeam", OnPlayerTryJoinTeam, HookMode.Pre);
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} COMEEEÇÇÇÇÇÇÇÇÇÇÇÇOOOOOOOOOOOOU!!!!!");
    }

    public HookResult HandlerOnDeathEvent(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var died = @event.Userid;


        if (attacker != null && died != null)
        {
            bool attackerIsCaptain = attacker == Captain1 || attacker == Captain2;
            bool diedIsCaptain = died == Captain1 || died == Captain2;
            if (!attackerIsCaptain || !diedIsCaptain) return HookResult.Continue;
        
            Captain1 = attacker;
            Captain2 = died;

            AddTimer(0.1f, () => {
            FinalizarX1(attacker);
            });
        }
        return HookResult.Continue;
    }

    [ConsoleCommand("css_x1win", "Debug command: Force x1 winner")]
    [RequiresPermissions("@css/rcon")]
    public void X1WinCommand(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;

        if (parameters == "2")
        {
            var temp = Captain1;
            Captain1 = Captain2;
            Captain2 = temp;
        } 
        
        if (Captain1 != null)
            FinalizarX1(Captain1);
    }

    public void FinalizarX1(CCSPlayerController vencedor)
    {
        DeregisterEventHandler<EventPlayerDeath>(HandlerOnDeathEvent);
        RemoveCommandListener("jointeam", OnPlayerTryJoinTeam, HookMode.Pre);
        
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Blue} {vencedor.PlayerName}{ChatColors.Default} ganhou o X1!");

        bool teamsChanged = vencedor.Team == CsTeam.Terrorist;
        
        if (teamsChanged && Captain1 != null && Captain2 != null)
        {
            ChangeTeamHandler(Captain1, CsTeam.CounterTerrorist);
            ChangeTeamHandler(Captain2, CsTeam.Terrorist);
        }
        else if (!teamsChanged && Captain1 != null && Captain2 != null)
            Captain1.Respawn();

        x1 = false;
        if (currentState == States.ChoosingCaptains)
            CaptainsPicking();
        else if (currentState == States.Randomizing || currentState == States.LevelRandomizing)
        {
            var playerIds = PlayersTeam.Keys.ToList();

            foreach (var steamId in playerIds)
            {
                CsTeam currentTeam = PlayersTeam[steamId];
                CsTeam newTeam = currentTeam;

                if (teamsChanged)
                {
                    newTeam = (currentTeam == CsTeam.CounterTerrorist) ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
                    PlayersTeam[steamId] = newTeam;
                }

                if (ulong.TryParse(steamId, out ulong steamId64))
                {
                    var player = Utilities.GetPlayerFromSteamId64(steamId64);
                    if (player != null)
                        ChangeTeamHandler(player, newTeam);
                }
                else
                {
                    var player = Utilities.GetPlayers().FirstOrDefault(p => p.PlayerName.Contains(steamId, StringComparison.OrdinalIgnoreCase));
                    if (player != null)
                        ChangeTeamHandler(player, newTeam);
                }
            }

            //Server.ExecuteCommand("mp_restartgame 1");
            CurrentPickTurn = 1;
            pickOrderIndex = 0;
            currentState = States.MapVeto;
            MapVeto();
        }

    }

    [ConsoleCommand("css_pick2", "Debug command: Pick bypass captain order")]
    [RequiresPermissions("@css/rcon")]
    public void Pick2Command(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters)) return;
        
        PickCommand(player, parameters, true);
    }

    public void CaptainsPicking()
    {
        currentState = States.CaptainsPicking;
        AddCommandListener("jointeam", OnPlayerTryJoinTeam, HookMode.Pre);
        if (_api != null)
            RegisterListener<Listeners.OnTick>(OnTick);

        DeregisterEventHandler<EventPlayerDeath>(HandlerOnDeathEvent);
        pickOrderIndex = 0;
        CurrentPickTurn = 1;
        if ((Captain1 != null && Captain1.IsValid) && (Captain2 != null && Captain2.IsValid))
        {
            if (ulong.TryParse(Captain1.SteamID.ToString(), out ulong captain1SteamId64) && captain1SteamId64 != 0)
                PlayersTeam[Captain1.SteamID.ToString()] = CsTeam.CounterTerrorist;
            else
                PlayersTeam[Captain1.PlayerName] = CsTeam.CounterTerrorist;

            if (ulong.TryParse(Captain2.SteamID.ToString(), out ulong captain2SteamId64) && captain2SteamId64 != 0)
                PlayersTeam[Captain2.SteamID.ToString()] = CsTeam.Terrorist;
            else
                PlayersTeam[Captain2.PlayerName] = CsTeam.Terrorist;
        }

        if (bots)
            PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsHLTV && p != Captain1 && p != Captain2)
                                                  .Where(p => !PlayersTeam.ContainsKey(p.SteamID.ToString()))
                                                  .Where(p => !PlayersTeam.ContainsKey(p.PlayerName)).ToList();
        else
            PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV && p != Captain1 && p != Captain2)
                                                  .Where(p => !PlayersTeam.ContainsKey(p.SteamID.ToString())).ToList();

        foreach (var player in PlayersToPick)
        {
            ChangeTeamHandler(player, CsTeam.Spectator);
        }

        if (_api != null)
            RegisterListener<Listeners.OnTick>(OnTick);
        ShowPickingMenu();
    }

    public void ShowPickingMenu()
    {
        var activeCaptain = (CurrentPickTurn == 1) ? Captain1 : Captain2;
        if (bots)
            PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsHLTV && p != Captain1 && p != Captain2)
                                                  .Where(p => !PlayersTeam.ContainsKey(p.SteamID.ToString()))
                                                  .Where(p => !PlayersTeam.ContainsKey(p.PlayerName)).ToList();
        else
            PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV && p != Captain1 && p != Captain2)
                                                  .Where(p => !PlayersTeam.ContainsKey(p.SteamID.ToString())).ToList();

        var index = 1;
        if (_api == null)
        {
            activeCaptain?.PrintToChat("--------------------------------");
            activeCaptain?.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Red} SUA VEZ DE ESCOLHER!{ChatColors.Default}");
            foreach (var player in PlayersToPick)
            {
                activeCaptain?.PrintToChat($" {ChatColors.Green} {index}{ChatColors.Default} -> {player.PlayerName}");
                index++;
            }

            activeCaptain?.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !pick <numero>{ChatColors.Default} para escolher!");
            activeCaptain?.PrintToChat("--------------------------------");
        } 
        else
        {
            if (activeCaptain == null) return;
            var menu = _api.GetMenu(" {Red}Escolha um Jogador");
            foreach (var player in PlayersToPick)
            {
                int currentIndex = index;
                menu.AddMenuOption($"{player.PlayerName}", (p, option) => {PickCommand(activeCaptain, currentIndex.ToString(), false);});
                index++;
            }

            menu.ExitButton = false;
            menu.PostSelectAction = PostSelectAction.Close;
            menu.Open(activeCaptain);
        }
    }

    [ConsoleCommand("css_pick", "Pick a player")]
    public void OnPickCommand(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters)) return;

        PickCommand(player, parameters, false);
    }

    public void PickCommand(CCSPlayerController? player, string parameters, bool debug)
    {
        if (currentState != States.CaptainsPicking) return;
        if (player == null) return;

        bool isCap1Turn = CurrentPickTurn == 1 && (debug || player == Captain1);
        bool isCap2Turn = CurrentPickTurn == 2 && (debug || player == Captain2);

        if (!isCap1Turn && !isCap2Turn && !debug) return;

        if (!int.TryParse(parameters, out int index)) return;

        if (index < 1 || index > PlayersToPick.Count)
        {
            player.PrintToChat($" {ChatColors.Red}[!] Número inválido! Escolha entre 1 e {PlayersToPick.Count}");
            return; 
        }

        var pickedPlayer = PlayersToPick[index-1];
        PlayersToPick.RemoveAt(index-1);

        if (isCap1Turn)
        {
            PickedPlayerHandler(pickedPlayer, Captain1, ChatColors.Blue, CsTeam.CounterTerrorist);
        }
        else
        {
            PickedPlayerHandler(pickedPlayer, Captain2, ChatColors.Orange, CsTeam.Terrorist);
        }

        if (PlayersToPick.Count == 0)
        {
            RemoveCommandListener("jointeam", OnPlayerTryJoinTeam, HookMode.Pre);
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Times definidos!");
            //Server.ExecuteCommand("mp_restartgame 1");
            CurrentPickTurn = 1;
            pickOrderIndex = 0;
            currentState = States.MapVeto;
            if (_api != null)
                RemoveListener<Listeners.OnTick>(OnTick);
            MapVeto();
        }
        else
        {
            pickOrderIndex++;
            string pickorder = pickOrder.ToString();
            if (pickOrderIndex >= pickorder.Length)
                pickOrderIndex = 0;
            
            char nextPickTurn = pickorder[pickOrderIndex];
            CurrentPickTurn = (nextPickTurn == 'A') ? 1 : 2;
            ShowPickingMenu();
        }
    }

    public void PickedPlayerHandler(CCSPlayerController pickedPlayer, CCSPlayerController? captain, char color, CsTeam team)
    {
        if (captain != null)
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{color} {captain.PlayerName}{ChatColors.Default} escolheu{color} {pickedPlayer.PlayerName} {ChatColors.Default}");
        var steamId = pickedPlayer.SteamID;
        if (steamId != 0)
        {
            PlayersTeam[pickedPlayer.SteamID.ToString()] = team;
        }
        else
        {
            PlayersTeam[pickedPlayer.PlayerName] = team;
        }

        ChangeTeamHandler(pickedPlayer, team);
    }

    public void MapVeto()
    {
        MapsRemaining = new List<string>(Config.MapPool);
        CurrentPickTurn = 1;
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Vai começar os vetos.");
        if (_api != null)
            RegisterListener<Listeners.OnTick>(OnTick);
        ShowVetoMenu();
    }

    public HookResult HandlerPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player != null && player.IsValid)
        {
            var steamId = player.SteamID;
            CsTeam team;
            if (steamId != 0 && PlayersTeam.TryGetValue(steamId.ToString(), out team))
            {
                PlayersTeam.Remove(steamId.ToString());
                Server.NextFrame(() => {
                    ChangeTeamHandler(player, team);
                });
            }

            bool hasHumans = PlayersTeam.Keys.Any(k => ulong.TryParse(k, out ulong id) && id > 0);

            if ((PlayersTeam.Count == 0 || !hasHumans) && currentState != States.Disabled)
            {
                currentState = States.Disabled;
                AddTimer(0.2f, () => {
                    DeregisterEventHandler<EventPlayerConnectFull>(HandlerPlayerConnectFull);
                    ClearData();
                    currentState = States.Disabled;
                    //Server.ExecuteCommand("mp_restartgame 1");
                    Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker] {ChatColors.Default} Todos os jogadores conectaram.");
                });
            }
        }
        return HookResult.Continue;
    }

    public void ShowVetoMenu()
    {
        var activeCaptain = (CurrentPickTurn == 1) ? Captain1 : Captain2;

        int index = 1;
        if (_api == null)
        {
            activeCaptain?.PrintToChat("--------------------------------");
            activeCaptain?.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Red} SUA VEZ DE BANIR!{ChatColors.Default} ");

            foreach (var map in MapsRemaining)
            {
                activeCaptain?.PrintToChat($" {ChatColors.Green} {index} ->{ChatColors.Default} {map}");
                index++;
            }

            activeCaptain?.PrintToChat($"Digite{ChatColors.Red} !ban <numero>{ChatColors.Default} para banir o mapa.");
            activeCaptain?.PrintToChat("--------------------------------");
        }
        else
        {
            if (activeCaptain == null) return;
            var menu = _api.GetMenu(" {Red}Banir um Mapa");
            foreach (var map in MapsRemaining)
            {
                int currentIndex = index;
                menu.AddMenuOption($"{map}", (p, option) => {BanCommand(activeCaptain, currentIndex.ToString(), false);});
                index++;
            }

            menu.ExitButton = false;
            menu.PostSelectAction = PostSelectAction.Close;
            menu.Open(activeCaptain);
        }
    }

    [ConsoleCommand("css_ban", "Ban a map")]
    public void OnBanCommand(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters)) return;

        BanCommand(player, parameters, false);
    }

    [ConsoleCommand("css_ban2", "Debug command: Ban bypass captain order")]
    [RequiresPermissions("@css/rcon")]
    public void Ban2Command(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters)) return;

        BanCommand(player, parameters, true);
    }

    public void BanCommand(CCSPlayerController? player, string parameters, bool debug)
    {
        if (currentState != States.MapVeto) return;
        if (player == null) return;

        bool isCap1Turn = CurrentPickTurn == 1 && (debug || player == Captain1);
        bool isCap2Turn = CurrentPickTurn == 2 && (debug || player == Captain2);
        if (!isCap1Turn && !isCap2Turn && !debug) return;

        if (!int.TryParse(parameters, out int index)) return;

        if (index < 1 || index > MapsRemaining.Count)
        {
            player.PrintToChat($" {ChatColors.Red}[!] Número inválido! Escolha entre 1 e {MapsRemaining.Count}");
            return; 
        }

        var bannedMap = MapsRemaining[index-1];
        MapsRemaining.RemoveAt(index-1);

        if (isCap1Turn)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Blue} {Captain1?.PlayerName}{ChatColors.Default} baniu{ChatColors.Blue} {bannedMap} {ChatColors.Default}");
        }
        else
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Orange} {Captain2?.PlayerName}{ChatColors.Default} baniu{ChatColors.Orange} {bannedMap} {ChatColors.Default}");
        }

        if (MapsRemaining.Count == 1)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Red} {MapsRemaining[0]}{ChatColors.Default} foi o mapa escolhido!");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Trocando o mapa em 3 segundo!");
            IniciarContagemRegressiva(3, TrocarMapa);
        }
        else
        {
            CurrentPickTurn = (CurrentPickTurn == 1) ? 2 : 1;
            ShowVetoMenu();
        }
    }

    public void TrocarMapa()
    {
        CurrentPickTurn = 1;
        pickOrderIndex = 0;
        //currentState = States.Disabled;
        currentState = States.GettingReady;
        if (_api != null)
            RemoveListener<Listeners.OnTick>(OnTick);
        RegisterEventHandler<EventPlayerConnectFull>(HandlerPlayerConnectFull);
        Server.ExecuteCommand($"map {MapsRemaining[0]}");
    }

    public void RandomTeamPicker()
    {
        currentState = States.Randomizing;

        List<CCSPlayerController>? players;
        if (bots)
            players = Utilities.GetPlayers().Where(p => !p.IsHLTV).ToList();
        else
            players = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV).ToList();

        if (players.Count < 2)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Jogadores insuficientes!");
            currentState = States.Active;
            return;
        }

        Random rnd = new Random();
        var randomPlayers = players.OrderBy(x => rnd.Next()).ToList();
        int mid = randomPlayers.Count / 2;

        var index = 0;
        foreach (var player in randomPlayers)
        {
            var team = (index < mid) ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
            ChangeTeamHandler(player, team);

            var steamId = player.SteamID;
            if (steamId != 0)
                PlayersTeam[player.SteamID.ToString()] = team;
            else
                PlayersTeam[player.PlayerName] = team;
            index++;
        }
        Captain1 = randomPlayers[0];
        Captain2 = randomPlayers[mid];
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Times definidos!");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 1: {ChatColors.Blue} {Captain1?.PlayerName}{ChatColors.Default} (CT)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 2: {ChatColors.Orange} {Captain2?.PlayerName}{ChatColors.Default} (TR)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !random{ChatColors.Default} randomizar os times.");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !tp start{ChatColors.Default} para confirmar.");
    }

    [ConsoleCommand("css_random", "Redo randomize")]
    public void RandomCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (currentState == States.Randomizing)
            RandomTeamPicker();
        else if (currentState == States.ChoosingCaptains)
            RandomCaptains();
        else if (currentState == States.LevelRandomizing)
            LevelRandomTeamPicker();
    }

    public void GettingPlayersLevel()
    {
        currentState = States.GettingPlayerLevel;

        List<CCSPlayerController>? players;
        if (bots)
            players = Utilities.GetPlayers().Where(p => !p.IsHLTV).ToList();
        else
            players = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV).ToList();

        List<Task> loadingTasks = new List<Task>();

        foreach (var player in players)
        {
            if (!player.IsValid) return;

            int level;
            if (player.IsBot)
            {
                Random random = new Random();
                level = random.Next(1, 21);
                PlayersLevel[player.PlayerName] = level;
            }
            else
            {
                if (!PlayersLevel.ContainsKey(player.SteamID.ToString()))
                    loadingTasks.Add(LoadPlayerLevel(player));
            }
        }

        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Carregando o level dos jogadores...");
        Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(loadingTasks);

                Server.NextFrame(() =>
                {
                    ShowPlayersLevel();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TeamPicker Error] Erro ao aguardar tasks: {ex.Message}");
            }
        });
    }

    public void ShowPlayersLevel()
    {
        List<CCSPlayerController>? players;
        if (bots)
            players = Utilities.GetPlayers().Where(p => !p.IsHLTV).ToList();
        else
            players = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV).ToList();

        var index = 1;
        Server.PrintToChatAll("--------------------------------");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Red} LEVEL DA GALERA!{ChatColors.Default}");
        foreach (var player in players)
        {
            int level;
            if (player.IsBot)
                level = PlayersLevel.GetValueOrDefault(player.PlayerName);
            else
                level = PlayersLevel.GetValueOrDefault(player.SteamID.ToString());
            Server.PrintToChatAll($" {ChatColors.Green} {player.PlayerName}{ChatColors.Default} -> {level}");
            index++;
        }

        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !level <numero>{ChatColors.Default} para alterar seu level!");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !showlevel{ChatColors.Default} para mostrar a lista novamente.");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !tp start{ChatColors.Default} para confirmar.");
        Server.PrintToChatAll("--------------------------------");
    }

    [ConsoleCommand("css_showlevel", "Show players level")]
    public void ShowLevelCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (currentState != States.GettingPlayerLevel) return;
        ShowPlayersLevel();
    }

    private async Task LoadPlayerLevel(CCSPlayerController player)
    {
        try
        {
            using var conn = new MySqlConnection(Config.ConnectionString);
            await conn.OpenAsync();

            string sql = "SELECT level FROM gc_levels WHERE steamid = @steamid LIMIT 1";
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@steamid", player.SteamID);

            var result = await cmd.ExecuteScalarAsync();

            if (result != null)
            {
                int level = Convert.ToInt32(result);
                PlayersLevel[player.SteamID.ToString()] = level;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TeamPicker SQL Load Error] {ex.Message}");
        }
    }

    [ConsoleCommand("css_level", "Set player level")]
    public void SetLevelCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (currentState != States.GettingPlayerLevel) return;
        if (player == null || !player.IsValid) return;

        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters))
            return;

        if (int.TryParse(command.GetArg(1), out int newLevel))
        {
            if (newLevel < 1 || newLevel > 21)
            {
                player.PrintToChat($" {ChatColors.Green}[TeamPicker]{ChatColors.Default} Nível inválido. Digite um número entre 1 e 21.");
                return;
            }

            PlayersLevel[player.SteamID.ToString()] = newLevel;

            Task.Run(async () => await SaveLevelToDb(player.SteamID, newLevel));

            player.PrintToChat($" {ChatColors.Green}[TeamPicker]{ChatColors.Default} Seu nível foi atualizado para: {ChatColors.Red}{newLevel}");
        }
        else
        {
            player.PrintToChat($" {ChatColors.Green}[TeamPicker]{ChatColors.Default} Por favor, digite apenas números.");
        }
    }

    private async Task SaveLevelToDb(ulong steamId, int level)
    {
        try
        {
            using var conn = new MySqlConnection(Config.ConnectionString);
            await conn.OpenAsync();

            string sql = @"
                INSERT INTO gc_levels (steamid, level, updated_at) 
                VALUES (@steamid, @level, NOW()) 
                ON DUPLICATE KEY UPDATE level = @level, updated_at = NOW();";

            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@steamid", steamId);
            cmd.Parameters.AddWithValue("@level", level);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TeamPicker SQL Save Error] {ex.Message}");
        }
    }

    public class Jogador
    {
        public string Id { get; set; } = "";
        public int Nivel { get; set; } = 0;
    }

    public void LevelRandomTeamPicker(int tolerancia = 1)
    {
        currentState = States.LevelRandomizing;

        // Converte o dicionário para uma Lista de objetos Jogador
        var listaJogadores = PlayersLevel.Select(x => new Jogador { Id = x.Key, Nivel = x.Value }).ToList();

        // 1. Aleatoriedade total no início (Shuffle - Algoritmo Fisher-Yates)
        Random rng = new Random();
        var randomPlayers = listaJogadores.OrderBy(x => rng.Next()).ToList();
        int n = randomPlayers.Count;
        while (n > 1)
        {
            n--;
            int k = rng.Next(n + 1);
            var value = randomPlayers[k];
            randomPlayers[k] = randomPlayers[n];
            randomPlayers[n] = value;
        }

        // Divide ao meio
        int meio = randomPlayers.Count / 2;
        List<Jogador> timeA = randomPlayers.Take(meio).ToList();
        List<Jogador> timeB = randomPlayers.Skip(meio).ToList();

        // 2. Loop de Balanceamento
        int maxTentativas = 100;

        for (int tentativa = 0; tentativa < maxTentativas; tentativa++)
        {
            int forcaA = timeA.Sum(j => j.Nivel);
            int forcaB = timeB.Sum(j => j.Nivel);
            int diferenca = forcaA - forcaB;

            // Se a diferença já for aceitável, paramos
            if (Math.Abs(diferenca) <= tolerancia)
                break;

            // Lógica de Troca
            (int indexA, int indexB)? melhorTroca = null;
            int menorDiferencaEncontrada = Math.Abs(diferenca);

            // Testa todas as trocas possíveis
            for (int i = 0; i < timeA.Count; i++)
            {
                for (int j = 0; j < timeB.Count; j++)
                {
                    int jA_Nivel = timeA[i].Nivel;
                    int jB_Nivel = timeB[j].Nivel;

                    // Calcula hipoteticamente a nova força
                    int novaForcaA = forcaA - jA_Nivel + jB_Nivel;
                    int novaForcaB = forcaB - jB_Nivel + jA_Nivel;
                    int novaDiff = Math.Abs(novaForcaA - novaForcaB);

                    // Se essa troca melhora o equilíbrio, guardamos os índices
                    if (novaDiff < menorDiferencaEncontrada)
                    {
                        menorDiferencaEncontrada = novaDiff;
                        melhorTroca = (i, j);
                    }
                }
            }

            // Se achou uma troca que melhora, executa
            if (melhorTroca.HasValue)
            {
                int idxA = melhorTroca.Value.indexA;
                int idxB = melhorTroca.Value.indexB;

                // Realiza a troca nas listas
                var temp = timeA[idxA];
                timeA[idxA] = timeB[idxB];
                timeB[idxB] = temp;
            }
            else
            {
                // Máximo local atingido (nenhuma troca melhora o cenário atual)
                break;
            }
        }

        ExibirTime("Time A", timeA);
        ExibirTime("Time B", timeB);

        foreach (var jogador in timeA)
        {
            CCSPlayerController? player = null;
            if (ulong.TryParse(jogador.Id, out ulong player_id))
                player = Utilities.GetPlayerFromSteamId(player_id);
            else
                player = Utilities.GetPlayers().FirstOrDefault(p => p.PlayerName.Contains(jogador.Id, StringComparison.OrdinalIgnoreCase));

            
            if (player != null && player.IsValid)
            {
                var steamId = player.SteamID;
                if (steamId != 0)
                    PlayersTeam[player.SteamID.ToString()] = CsTeam.CounterTerrorist;
                else
                    PlayersTeam[player.PlayerName] = CsTeam.CounterTerrorist;
                ChangeTeamHandler(player, CsTeam.CounterTerrorist);
            }
        }

        foreach (var jogador in timeB)
        {
            CCSPlayerController? player = null;
            if (ulong.TryParse(jogador.Id, out ulong player_id))
                player = Utilities.GetPlayerFromSteamId(player_id);
            else
                player = Utilities.GetPlayers().FirstOrDefault(p => p.PlayerName.Contains(jogador.Id, StringComparison.OrdinalIgnoreCase));

            
            if  (player != null && player.IsValid)
            {
                var steamId = player.SteamID;
                if (steamId != 0)
                    PlayersTeam[player.SteamID.ToString()] = CsTeam.Terrorist;
                else
                    PlayersTeam[player.PlayerName] = CsTeam.Terrorist;
                ChangeTeamHandler(player, CsTeam.Terrorist);
            }
        }
        
        if (ulong.TryParse(timeA[0].Id, out ulong captain1SteamId))
            Captain1 = Utilities.GetPlayerFromSteamId(captain1SteamId);
        else
            Captain1 = Utilities.GetPlayers().FirstOrDefault(p => p.PlayerName.Contains(timeA[0].Id, StringComparison.OrdinalIgnoreCase));
        if (ulong.TryParse(timeB[0].Id, out ulong captain2SteamId))
            Captain2 = Utilities.GetPlayerFromSteamId(captain2SteamId);
        else
            Captain2 = Utilities.GetPlayers().FirstOrDefault(p => p.PlayerName.Contains(timeB[0].Id, StringComparison.OrdinalIgnoreCase));
        
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 1: {ChatColors.Blue} {Captain1?.PlayerName}{ChatColors.Default} (CT)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 2: {ChatColors.Orange} {Captain2?.PlayerName}{ChatColors.Default} (TR)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !random{ChatColors.Default} randomizar os times.");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !tp start{ChatColors.Default} para confirmar.");
    }

    public void ExibirTime(string nomeTime, List<Jogador> time)
    {
        int soma = time.Sum(j => j.Nivel);
        double media = time.Count > 0 ? (double)soma / time.Count : 0;
        
        var listaNomes = new List<string>();

        foreach (var jogador in time)
        {
            string nome = jogador.Id;

            if (ulong.TryParse(jogador.Id, out ulong targetSteamId64))
            {
                CCSPlayerController? player = Utilities.GetPlayerFromSteamId(targetSteamId64);
                
                if (player != null && !string.IsNullOrEmpty(player.PlayerName))
                {
                    nome = player.PlayerName;
                }
            }

            listaNomes.Add($"{nome} ({jogador.Nivel})");
        }

        Server.PrintToChatAll("--------------------------------");
        Server.PrintToChatAll($"[{nomeTime}] Força: {soma} | Média: {media:F1}");
        Server.PrintToChatAll($"Elenco: {string.Join(", ", listaNomes)}");
        Server.PrintToChatAll("--------------------------------");
    }

    public void ClearData()
    {
        try { DeregisterEventHandler<EventPlayerDeath>(HandlerOnDeathEvent); } catch {}
        try { DeregisterEventHandler<EventPlayerConnectFull>(HandlerPlayerConnectFull); } catch {}
        try { RemoveCommandListener("jointeam", OnPlayerTryJoinTeam, HookMode.Pre); } catch {}
        try { RemoveListener<Listeners.OnTick>(OnTick); } catch {}
        Captain1 = null;
        Captain2 = null;
        pickOrder = PickOrder.ABABABAB;
        pickOrderIndex = 0;
        RegisterListener<Listeners.OnTick>(OnTick);
        CurrentPickTurn = 1;
        x1 = false;
        //PlayersLevel.Clear();
        PlayersTeam.Clear();
        PlayersToPick.Clear();
        MapsRemaining.Clear();
    }

    public void OnTick()
    {
        if (_api == null) return;

        if (currentState == States.CaptainsPicking)
        {
            var activeCaptain = (CurrentPickTurn == 1) ? Captain1 : Captain2;
            if (activeCaptain != null && activeCaptain.IsValid && !activeCaptain.IsBot && activeCaptain.Connected == PlayerConnectedState.PlayerConnected)
            {
                 if (!_api.HasOpenedMenu(activeCaptain))
                 {
                    ShowPickingMenu();
                 }
            }
        }
        else if (currentState == States.MapVeto)
        {
            var activeCaptain = (CurrentPickTurn == 1) ? Captain1 : Captain2;
            if (activeCaptain != null && activeCaptain.IsValid && !activeCaptain.IsBot && activeCaptain.Connected == PlayerConnectedState.PlayerConnected)
            {
                if (!_api.HasOpenedMenu(activeCaptain))
                {
                    ShowVetoMenu();
                }
            }
        }
    }

    public void ChangeTeamHandler(CCSPlayerController player, CsTeam team)
    {
        if (team == player.Team) return;

        if (player.Team == CsTeam.Spectator || player.Team == CsTeam.None)
        {
            var otherTeam = team == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            player.ChangeTeam(otherTeam);
            Server.NextFrame(() =>
            {
                player.Respawn();
                Server.NextFrame (() =>
                {
                    player.ChangeTeam(team);
                });
            });
        }
        else
            player.ChangeTeam(team);
    }
}
