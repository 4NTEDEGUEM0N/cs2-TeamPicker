# 🔫 CS2 TeamPicker

![CS2](https://img.shields.io/badge/Game-CS2-orange?style=for-the-badge&logo=counter-strike)
![Platform](https://img.shields.io/badge/Platform-CounterStrikeSharp-blueviolet?style=for-the-badge)
![Status](https://img.shields.io/badge/Status-Active-brightgreen?style=for-the-badge)

**TeamPicker** é um plugin para Counter-Strike 2 focado em organizar partidas (PUGs/Scrims/Mix). Com sistemas de escolha de times, veto de mapas e duelos X1.

---

## 🔥 Funcionalidades

*   **🎓 Modos de Seleção:**
    *   **Captains:** Dois capitães escolhem os jogadores, um a um.
    *   **Random:** Times sorteados aleatoriamente.
    *   **Level:** Balanceamento inteligente baseado em skill (MySQL).
*   **⚔️ Duelo X1 (Deagle):**
    *   Decisão de lados/picks através de um duelo 1v1 emocionante.
    *   Arenas aleatórias (Bombsite A, B ou Meio).
    *   Loadout automático: Faca + Deagle.
*   **🗺️ Map Veto System:**
    *   Veto de mapas direto pelo chat.
    *   Map Pool totalmente configurável.
*   **🤖 Bot Support:**
    *   Permite incluir bots na seleção para completar times ou testar.
*   **⚙️ Configuração Dinâmica:**
    *   Ordem de picks personalizável (ABAB, ABBA...).
    *   Integração com MySQL para persistência de dados.

---

## 🛠️ Instalação

1.  Instale o **[CounterStrikeSharp](https://docs.cssharp.dev/)**.
2.  Baixe a última release do **TeamPicker**.
3.  Descompacte na pasta `addons/counterstrikesharp/plugins/`.
    *   Estrutura recomendada: `.../plugins/TeamPicker/TeamPicker.dll`
4.  Reinicie o servidor.
5.  O arquivo de configuração `TeamPicker.json` será criado automaticamente.

---

## 🎮 Comandos do Chat

### 🕹️ Principais
| Comando | Função |
| :--- | :--- |
| `!tp` | Painel principal / Status |
| `!tp start` | Inicia o processo (X1/Picks) |
| `!tp restart` | Reinicia o plugin |
| `!tp disable` | Desativa o plugin |
| `!help` | Mostra ajuda contextual |

### 🎲 Modos de Jogo/Opções
| Comando | Descrição |
| :--- | :--- |
| `!tp captains` | Ativa o modo **Captains** (Padrão) |
| `!tp random` | Ativa o modo **Aleatório** |
| `!tp level` | Ativa o modo **Level** (Requer DB) |
| `!tp bots` | Ativa/Desativa bots no draft |

### 👑 Comandos de Capitão (Global)
| Comando | Descrição |
| :--- | :--- |
| `!captain1 <nome>` | Força um jogador como Capitão 1 (CT) |
| `!captain2 <nome>` | Força um jogador como Capitão 2 (TR) |
| `!pickorder` | Alterna a ordem de escolha (Ex: ABAB, ABBA) |

### ⚡ Durante o Draft/Veto
| Comando | Descrição |
| :--- | :--- |
| `!pick <n>` | Escolhe o jogador número `n` da lista |
| `!ban <n>` | Veta o mapa número `n` da lista |

---

## ⚙️ Configuração

Edite o arquivo `counterstrikesharp/configs/plugins/TeamPicker/TeamPicker.json`:

```json
{
  "ConfigVersion": 3,
  "DbHost": "127.0.0.1",
  "DbPort": "3306",
  "DbUser": "seu_usuario",
  "DbPassword": "sua_senha",
  "DbName": "sua_db",
  "MapPool": [
    "de_mirage",
    "de_inferno",
    "de_nuke",
    "de_overpass",
    "de_dust2",
    "de_ancient",
    "de_anubis"
  ]
}
```