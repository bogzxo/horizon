# Shell integration for zsh. The terminal (Dusk, Hex's terminal page) starts zsh with ZDOTDIR pointing here, and the
# user's own (or nothing, for their home) in DUSK_USER_ZDOTDIR, so these files are read in place of theirs and read
# theirs in turn, never written into anybody's dotfiles. This one is read by every zsh, first.

__dusk_zdotdir="$ZDOTDIR"
ZDOTDIR="${DUSK_USER_ZDOTDIR:-$HOME}"
[[ -f "$ZDOTDIR/.zshenv" ]] && source "$ZDOTDIR/.zshenv"

# Their .zshenv may say ZDOTDIR is elsewhere, which is where the rest of theirs is
[[ "$ZDOTDIR" != "${DUSK_USER_ZDOTDIR:-$HOME}" ]] && DUSK_USER_ZDOTDIR="$ZDOTDIR"
ZDOTDIR="$__dusk_zdotdir"
