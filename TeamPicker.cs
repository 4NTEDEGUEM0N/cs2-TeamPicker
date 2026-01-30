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

namespace TeamPicker;

public class TeamPickerConfig : BasePluginConfig
{
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
}

public enum States
{
    Disabled,
    Active,
    ChoosingCaptains,
    X1,
    CaptainsPicking,
    GettingPlayerLevel,
    Randomizing,
    MapVeto
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
        Logger.LogInformation("TeamPicker loaded successfully!");
    }

    public States currentState = States.Disabled;
    public Modes mode = Modes.Captians;
    public CCSPlayerController? Captain1 { get; set; }
    public CCSPlayerController? Captain2 { get; set; }
    public HookResult? deathHook;
    public PickOrder pickOrder = PickOrder.ABABABAB;
    public int pickOrderIndex = 0;
    public int CurrentPickTurn { get; set; } = 1; // 1 = Vez do Cap1, 2 = Vez do Cap2
    public List<(CCSPlayerController?, int)> PlayersLevel = new List<(CCSPlayerController?, int)>();
    public List<(CCSPlayerController?, CsTeam)> PlayersTeam = new List<(CCSPlayerController?, CsTeam)>();
    public List<CCSPlayerController> PlayersToPick = new List<CCSPlayerController>();

    public TeamPickerConfig Config { get; set; } = new TeamPickerConfig();
    public List<string> MapsRemaining { get; set; } = new List<string>();
    public void OnConfigParsed(TeamPickerConfig config)
    {
        this.Config = config;

        if (config.MapPool == null || config.MapPool.Count < 1)
        {
            config.MapPool = new List<string> { "de_mirage", "de_inferno", "de_nuke", "de_overpass", "de_vertigo", "de_ancient", "de_anubis"};
            Logger.LogWarning("MapPool estava vazio na config! Usando padrão.");
        }
        
        Logger.LogInformation($"Config carregada com {config.MapPool.Count} mapas.");
    }

    [ConsoleCommand("css_rcon", "Server RCON")]
    [RequiresPermissions("@css/rcon")]
    public void RconCommand(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters))
            return;
        Server.ExecuteCommand(parameters);
    }

    [ConsoleCommand("css_rename", "Rename a Player")]
    [RequiresPermissions("@css/rcon")]
    public void RenameCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 3) return;

        string targetSearch = command.ArgByIndex(1);
        string newName = command.ArgString.Substring(targetSearch.Length).Trim();

        player?.PrintToChat($"Target: {targetSearch} - Novo Nome: {newName}");

        var players = Utilities.GetPlayers();
        var target = players.FirstOrDefault(p => p.PlayerName.Contains(targetSearch, StringComparison.OrdinalIgnoreCase));
        if (target == null) return;
        player?.PrintToChat($"Achei o jogador -> {target.PlayerName}");
        target.PlayerName = newName;
        Utilities.SetStateChanged(target, "CBasePlayerController", "m_iszPlayerName");
        player?.PrintToChat($"Mudou o nome -> {target.PlayerName}");
    }

    [ConsoleCommand("css_tp", "Activate TeamPicker Plugin")]
    public void TeamPickerCommand(CCSPlayerController? player, CommandInfo command)
    {
        bool disabledOrActive =  currentState == States.Disabled || currentState != States.Active;

        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters) && disabledOrActive)
            mode = Modes.Captians;
        else if (string.Equals(parameters, "random", StringComparison.OrdinalIgnoreCase) && disabledOrActive)
            mode = Modes.Random;
        else if (string.Equals(parameters, "captains", StringComparison.OrdinalIgnoreCase) && disabledOrActive)
            mode = Modes.Captians;
        else if (string.Equals(parameters, "start", StringComparison.OrdinalIgnoreCase) && (currentState != States.Disabled))
        {
            Start();
            return;
        }
        else if (string.Equals(parameters, "restart", StringComparison.OrdinalIgnoreCase) && (currentState != States.Disabled))
        {
            currentState = States.Active;
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Restarted");
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Mode:{ChatColors.Red} {mode}{ChatColors.Default}");
            return;
        }
        else if (string.Equals(parameters, "disable", StringComparison.OrdinalIgnoreCase) && (currentState != States.Disabled))
        {
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
            currentState = States.Active;
        }
        else
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Mode changed to {ChatColors.Red} {mode}{ChatColors.Default}");
    }

    public void Start()
        {
            switch(mode)
            {
                case Modes.Captians:
                    if (currentState == States.Active)
                    {
                        currentState = States.ChoosingCaptains;
                        ChoosingCaptains();
                    }
                    else if (currentState == States.ChoosingCaptains)
                    {
                        currentState = States.X1;
                        X1();
                    }
                    break;
                case Modes.Level:
                    currentState = States.GettingPlayerLevel;
                    LevelTeamPicker();
                    break;
                case Modes.Random:
                    currentState = States.GettingPlayerLevel;
                    RandomTeamPicker();
                    break;
            }
            return;
        }

    public void ChoosingCaptains()
    {
        PlayersToPick = new List<CCSPlayerController>();
        PlayersTeam = new List<(CCSPlayerController?, CsTeam)>();
        CurrentPickTurn = 1;

        //var players = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV).ToList();
        var players = Utilities.GetPlayers().Where(p => !p.IsHLTV).ToList();

        if (players.Count < 2)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Jogadores insuficientes para iniciar capitães!");
            currentState = States.Active;
            return;
        }

        Random rnd = new Random();
        var randomPlayers = players.OrderBy(x => rnd.Next()).ToList();

        Captain1 = randomPlayers[0];
        Captain2 = randomPlayers[1];

        Captain1.ChangeTeam(CsTeam.CounterTerrorist); 
        Captain2.ChangeTeam(CsTeam.Terrorist);

        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 1: {ChatColors.Green} {Captain1.PlayerName}{ChatColors.Default}  (CT)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 2: {ChatColors.Green} {Captain2.PlayerName}{ChatColors.Default}  (TR)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Ordem de Picks: {ChatColors.Green} {pickOrder}{ChatColors.Default} ");
    }

    [ConsoleCommand("css_captain1", "Choose Captain1")]
    public void Captain1Command(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters))
            return;
        
        var players = Utilities.GetPlayers();
        var target = players.FirstOrDefault(p => p.PlayerName.Contains(parameters, StringComparison.OrdinalIgnoreCase));
        
        if (target != null && target != Captain2)
        {
            Captain1 = target;
            Captain1.ChangeTeam(CsTeam.CounterTerrorist); 
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Green} {target.PlayerName}{ChatColors.Default} é o Capitão 1");
        }
        else
        {
            if (player == null) return;
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Jogador não encontrado.");
        }
    }

    [ConsoleCommand("css_captain2", "Choose Captain1")]
    public void Captain2Command(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters))
            return;
        
        var players = Utilities.GetPlayers();
        var target = players.FirstOrDefault(p => p.PlayerName.Contains(parameters, StringComparison.OrdinalIgnoreCase));
        
        if (target != null && target != Captain2)
        {
            Captain2 = target;
            Captain2.ChangeTeam(CsTeam.Terrorist);
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Green} {target.PlayerName}{ChatColors.Default} é o Capitão 2");
        }
        else
        {
            if (player == null) return;
            player.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Jogador não encontrado.");
        }
    }

    [ConsoleCommand("css_captains", "Print current captains")]
    public void CaptainsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (currentState != States.ChoosingCaptains) return;

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
            foreach (string orders in Enum.GetNames(typeof(PickOrder)))
            {
                player?.PrintToChat($" {ChatColors.Green} {index}{ChatColors.Default} -> {orders}");
                index++;
            }
            player?.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Digite{ChatColors.Red} !pickorder <numero>{ChatColors.Default} para escolher!");
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
        if (segundos > 0)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Red}{segundos}{ChatColors.Default}...");
            
            AddTimer(1.0f, () => IniciarContagemRegressiva(segundos - 1, func));
        }
        else
        {
            func.Invoke();
        }
    }

    public void X1()
    {
        Server.ExecuteCommand("mp_ct_default_secondary weapon_deagle");
        Server.ExecuteCommand("mp_t_default_secondary weapon_deagle");
        //PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV && p != Captain1 && p != Captain2)
        //                            .Where(p => !PlayersTeam.Any(pt => pt.Item1 == p)).ToList();
        PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsHLTV && p != Captain1 && p != Captain2)
                                              .Where(p => !PlayersTeam.Any(pt => pt.Item1 == p)).ToList();

        foreach (var player in PlayersToPick)
        {
            player.ChangeTeam(CsTeam.Spectator);
        }
        List<string> arenas = ["Bombsite A", "Meio", "Bomsite B"];
        Random rnd = new Random();
        var randomArenas = arenas.OrderBy(x => rnd.Next()).ToList();
        var selectedArena = randomArenas[0];
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} O local da batalha é o{ChatColors.Red} {selectedArena}{ChatColors.Default} !");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} o X1 vai começar em 3 segundo!");
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

    public void FinalizarX1(CCSPlayerController vencedor)
    {
        DeregisterEventHandler<EventPlayerDeath>(HandlerOnDeathEvent);
        
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Blue} {vencedor.PlayerName}{ChatColors.Default} ganhou o X1!");
        
        Captain1?.ChangeTeam(CsTeam.CounterTerrorist);
        Captain2?.ChangeTeam(CsTeam.Terrorist);

        Server.ExecuteCommand("mp_ct_default_secondary weapon_hkp2000");
        Server.ExecuteCommand("mp_t_default_secondary weapon_glock");
        
        currentState = States.CaptainsPicking;
        CaptainsPicking();
    }

    [ConsoleCommand("css_pick2", "Debug command: Pick bypass captain order")]
    [RequiresPermissions("@css/rcon")]
    public void Pick2Command(CCSPlayerController? player, CommandInfo command)
    {
        PickCommand(player, command, true);
    }

    public void CaptainsPicking()
    {
        DeregisterEventHandler<EventPlayerDeath>(HandlerOnDeathEvent);
        pickOrderIndex = 0;
        CurrentPickTurn = 1;
        PlayersTeam.Add((Captain1, CsTeam.CounterTerrorist));
        PlayersTeam.Add((Captain2, CsTeam.Terrorist));
        //PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV && p != Captain1 && p != Captain2)
        //                            .Where(p => !PlayersTeam.Any(pt => pt.Item1 == p)).ToList();
        PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsHLTV && p != Captain1 && p != Captain2)
                                              .Where(p => !PlayersTeam.Any(pt => pt.Item1 == p)).ToList();

        foreach (var player in PlayersToPick)
        {
            player.ChangeTeam(CsTeam.Spectator);
        }

        ShowPickingMenu();
    }

    public void ShowPickingMenu()
    {
        var activeCaptain = (CurrentPickTurn == 1) ? Captain1 : Captain2;
        PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsHLTV && p != Captain1 && p != Captain2)
                                              .Where(p => !PlayersTeam.Any(pt => pt.Item1 == p)).ToList();

        var index = 1;
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

    [ConsoleCommand("css_pick", "Pick a player")]
    public void OnPickCommand(CCSPlayerController? player, CommandInfo command)
    {
        PickCommand(player, command, false);
    }

    public void PickCommand(CCSPlayerController? player, CommandInfo command, bool debug)
    {
        if (currentState != States.CaptainsPicking) return;
        if (player == null) return;

        bool isCap1Turn = CurrentPickTurn == 1 && player == Captain1;
        bool isCap2Turn = CurrentPickTurn == 2 && player == Captain2;
        if (!isCap1Turn && !isCap2Turn && !debug) return;

        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters)) return;

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
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Blue} {Captain1?.PlayerName}{ChatColors.Default} escolheu{ChatColors.Blue} {pickedPlayer.PlayerName} {ChatColors.Default}");
            PlayersTeam.Add((pickedPlayer, CsTeam.CounterTerrorist));
            pickedPlayer.ChangeTeam(CsTeam.CounterTerrorist);
        }
        else
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Orange} {Captain2?.PlayerName}{ChatColors.Default} escolheu{ChatColors.Orange} {pickedPlayer.PlayerName} {ChatColors.Default}");
            PlayersTeam.Add((pickedPlayer, CsTeam.Terrorist));
            pickedPlayer.ChangeTeam(CsTeam.Terrorist);
        }

        if (PlayersToPick.Count == 0)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Times definidos!");
            Server.ExecuteCommand("mp_restartgame 1");
            CurrentPickTurn = 1;
            pickOrderIndex = 0;
            currentState = States.MapVeto;
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

    public void LevelTeamPicker()
    {
        currentState = States.Disabled;
        return;
    }

    public void RandomTeamPicker()
    {
        currentState = States.Disabled;
        return;
    }

    public void MapVeto()
    {
        MapsRemaining = new List<string>(Config.MapPool);
        CurrentPickTurn = 1;
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Vai começar os vetos.");
        ShowVetoMenu();
    }

    public void ShowVetoMenu()
    {
        var activeCaptain = (CurrentPickTurn == 1) ? Captain1 : Captain2;

        activeCaptain?.PrintToChat("--------------------------------");
        activeCaptain?.PrintToChat($" {ChatColors.Green} [TeamPicker]{ChatColors.Red} SUA VEZ DE BANIR!{ChatColors.Default} ");

        int index = 1;
        foreach (var map in MapsRemaining)
        {
            activeCaptain?.PrintToChat($" {ChatColors.Green} {index} ->{ChatColors.Default} {map}");
            index++;
        }

        activeCaptain?.PrintToChat($"Digite{ChatColors.Red} !ban <numero>{ChatColors.Default} para banir o mapa.");
        activeCaptain?.PrintToChat("--------------------------------");
    }

    [ConsoleCommand("css_ban", "Ban a map")]
    public void OnBanCommand(CCSPlayerController? player, CommandInfo command)
    {
        BanCommand(player, command, false);
    }

    [ConsoleCommand("css_ban2", "Debug command: Ban bypass captain order")]
    [RequiresPermissions("@css/rcon")]
    public void Ban2Command(CCSPlayerController? player, CommandInfo command)
    {
        BanCommand(player, command, true);
    }

    public void BanCommand(CCSPlayerController? player, CommandInfo command, bool debug)
    {
        if (currentState != States.MapVeto) return;
        if (player == null) return;

        bool isCap1Turn = CurrentPickTurn == 1 && player == Captain1;
        bool isCap2Turn = CurrentPickTurn == 2 && player == Captain2;
        if (!isCap1Turn && !isCap2Turn && !debug) return;

        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters)) return;

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
        currentState = States.Disabled;
        Server.ExecuteCommand($"map {MapsRemaining[0]}");
    }
}
