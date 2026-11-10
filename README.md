# overwatch-hud-on (AutoOverlay)

Affiche des overlays PNG (cadres de HUD, viseurs, guides) par-dessus Overwatch, **uniquement quand tu es réellement en jeu**. Dans les menus, au tableau des scores, en fin de manche ou hors du jeu, l'overlay disparaît tout seul.

## Fonctionnement

- Une fenêtre transparente, toujours au premier plan et traversée par les clics. **Aucune injection dans le jeu, aucune lecture de sa mémoire.**
- L'overlay s'affiche si Overwatch est la fenêtre active **et** si le HUD est à l'écran. Le HUD est reconnu grâce au badge de niveau bleu sous la barre de vie (ou au bandeau orange de choix d'atout qui le remplace), lu sur une zone d'environ 26 x 23 pixels de l'écran.
- Hors jeu, la surveillance passe à 4 vérifications par seconde.

## Installation

1. Windows 10/11, rien à installer : le compilateur C# fourni avec Windows suffit.
2. Compiler :
   ```powershell
   powershell -ExecutionPolicy Bypass -File build.ps1
   ```
3. Lancer `AutoOverlay.exe`. Une icône orange apparaît dans la zone de notification.
4. Clic droit sur l'icône > **Ajouter des overlays...** (ou déposer des PNG dans `overlays\` puis **Recharger**).

Les images sont centrées sur la fenêtre du jeu : un PNG à la résolution de l'écran (1920x1080 par exemple) se superpose donc exactement.

## Menu de l'icône

- Cocher/décocher chaque overlay
- Masquer dans les menus (curseur visible)
- Lancer au démarrage de Windows
- Ajouter des overlays, aperçu 5 secondes, ouvrir le dossier ou la configuration, recharger

## Configuration (`config.ini`)

Créé au premier lancement. Après modification : **Recharger**.

| Clé | Rôle |
| --- | --- |
| `processus` | Processus de jeu surveillés, séparés par des virgules |
| `raccourci` | Inverse l'affichage à la main (ex. `F8`, `Ctrl+F8`, `Oem1`) |
| `raccourci_suivant` | Passe à l'overlay suivant (vide = désactivé) |
| `detection_hud` | Exige la présence du HUD d'Overwatch |
| `delai_ms` / `delai_masquage_ms` | Délai avant affichage / avant masquage |
| `intervalle_ms` | Fréquence de vérification en jeu |
| `[image.png]` | Par image : `actif`, `decalage_x`, `decalage_y`, `echelle`, `opacite` |

## Limites

- Le jeu doit être en **fenêtré sans bordure** (ou plein écran avec les optimisations de Windows) : une fenêtre ne peut pas s'afficher par-dessus un plein écran exclusif.
- La détection du HUD est calibrée pour un affichage 16:9 et la couleur d'équipe bleue par défaut.
- Mort ou en spectateur, le HUD est assombri : l'overlay se masque aussi.

Les overlays eux-mêmes ne sont pas fournis dans ce dépôt.
