# Shell integration for zsh, see .zshenv. A login shell's profile (a Mac's terminals start login shells), the user's own.

ZDOTDIR="${DUSK_USER_ZDOTDIR:-$HOME}"
[[ -f "$ZDOTDIR/.zprofile" ]] && source "$ZDOTDIR/.zprofile"
ZDOTDIR="$__dusk_zdotdir"
