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
namespace TeamPicker;

public enum States
{
    Disabled,
    Active,
    ChoosingCaptains,
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

public class TeamPicker : BasePlugin
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
    public int CurrentPickTurn { get; set; } = 1; // 1 = Vez do Cap1, 2 = Vez do Cap2
    public List<(CCSPlayerController?, int)> PlayersLevel = new List<(CCSPlayerController?, int)>();
    public List<(CCSPlayerController?, CsTeam)> PlayersTeam = new List<(CCSPlayerController?, CsTeam)>();
    public List<CCSPlayerController> PlayersToPick = new List<CCSPlayerController>();

    [ConsoleCommand("css_rcon", "Server RCON")]
    [RequiresPermissions("@css/rcon")]
    public void RconCommand(CCSPlayerController? player, CommandInfo command)
    {
        string parameters = command.ArgString;
        if (string.IsNullOrWhiteSpace(parameters))
            return;
        Server.ExecuteCommand(parameters);
    }

    [ConsoleCommand("css_teampicker", "Activate TeamPicker Plugin")]
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
                        currentState = States.CaptainsPicking;
                        CaptainsPicking();
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

        Captain1.SwitchTeam(CsTeam.CounterTerrorist); 
        Captain2.SwitchTeam(CsTeam.Terrorist);

        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 1: {ChatColors.Green} {Captain1.PlayerName}{ChatColors.Default}  (CT)");
        Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Capitão 2: {ChatColors.Green} {Captain2.PlayerName}{ChatColors.Default}  (TR)");
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
            Captain1.SwitchTeam(CsTeam.CounterTerrorist); 
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
            Captain2.SwitchTeam(CsTeam.Terrorist);
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
    }

    [ConsoleCommand("css_pick2", "Debug command: Pick for captains2")]
    [RequiresPermissions("@css/rcon")]
    public void Captain2PickCommand(CCSPlayerController? player, CommandInfo command)
    {
        PickCommand(player, command, true);
    }

    public void CaptainsPicking()
    {
        CurrentPickTurn = 1;
        PlayersTeam.Add((Captain1, CsTeam.CounterTerrorist));
        PlayersTeam.Add((Captain2, CsTeam.Terrorist));
        //PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV && p != Captain1 && p != Captain2)
        //                            .Where(p => !PlayersTeam.Any(pt => pt.Item1 == p)).ToList();
        PlayersToPick = Utilities.GetPlayers().Where(p => !p.IsHLTV && p != Captain1 && p != Captain2)
                                              .Where(p => !PlayersTeam.Any(pt => pt.Item1 == p)).ToList();

        foreach (var player in PlayersToPick)
        {
            player.SwitchTeam(CsTeam.Spectator);
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
            pickedPlayer.SwitchTeam(CsTeam.CounterTerrorist);
            pickedPlayer.Respawn();
        }
        else
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Blue} {Captain2?.PlayerName}{ChatColors.Default} escolheu{ChatColors.Blue} {pickedPlayer.PlayerName} {ChatColors.Default}");
            PlayersTeam.Add((pickedPlayer, CsTeam.Terrorist));
            pickedPlayer.SwitchTeam(CsTeam.Terrorist);
            pickedPlayer.Respawn();
        }

        if (PlayersToPick.Count == 0)
        {
            Server.PrintToChatAll($" {ChatColors.Green} [TeamPicker]{ChatColors.Default} Times definidos!");
            CurrentPickTurn = 1;
            currentState = States.MapVeto;
            MapVeto();
        }
        else
        {
            CurrentPickTurn = (CurrentPickTurn == 1) ? 2 : 1;
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
        currentState = States.Disabled;
        return;
    }
}
