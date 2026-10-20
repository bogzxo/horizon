# Shell integration for zsh, see .zshenv. The user's own .zshrc, then the marks and the commands of dusk.zsh. ZDOTDIR is
# theirs again after it, so a login shell's .zlogin is theirs, and a zsh started inside this one is plainly theirs.

ZDOTDIR="${DUSK_USER_ZDOTDIR:-$HOME}"
[[ -f "$ZDOTDIR/.zshrc" ]] && source "$ZDOTDIR/.zshrc"

source "${__dusk_zdotdir:h}/dusk.zsh"

if [[ -n "$DUSK_USER_ZDOTDIR" ]]; then
    ZDOTDIR="$DUSK_USER_ZDOTDIR"
else
    unset ZDOTDIR
fi
unset DUSK_USER_ZDOTDIR __dusk_zdotdir
