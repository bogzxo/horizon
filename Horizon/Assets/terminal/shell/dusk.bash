# Shell integration for bash, given to the shell by the terminal (Dusk, Hex's terminal page) with --init-file as it
# starts and never written into anybody's profile. The login files and .bashrc are read first as a login shell would,
# then it says where the prompt starts and ends, where a command's output starts and how the command ended (FinalTerm's
# OSC 133, A B C D) and which folder the shell is in (OSC 7).

[ -f /etc/profile ] && . /etc/profile
if [ -f ~/.bash_profile ]; then . ~/.bash_profile
elif [ -f ~/.bash_login ]; then . ~/.bash_login
elif [ -f ~/.profile ]; then . ~/.profile
elif [ -f ~/.bashrc ]; then . ~/.bashrc
fi

if [ -z "$__dusk_integrated" ]; then
    __dusk_integrated=1
    __dusk_ran=

    __dusk_prompt() {
        local code=$?
        if [ -n "$__dusk_ran" ]; then printf '\e]133;D;%s\a' "$code"; fi
        __dusk_ran=
        printf '\e]7;file://%s%s\a' "${HOSTNAME:-localhost}" "$PWD"
        printf '\e]133;A\a'
        __dusk_at_prompt=1
    }

    __dusk_preexec() {
        if [ -n "$__dusk_at_prompt" ] && [ "$BASH_COMMAND" != "__dusk_prompt" ]; then
            __dusk_at_prompt=
            __dusk_ran=1
            printf '\e]133;C\a'
        fi
    }

    PROMPT_COMMAND="__dusk_prompt${PROMPT_COMMAND:+;$PROMPT_COMMAND}"
    PS1="$PS1\[\e]133;B\a\]"
    trap '__dusk_preexec' DEBUG
fi

# What commands the shell has (every program on its PATH, its builtins, functions and aliases), written in the
# background so the prompt isn't kept waiting, for the terminal to colour the first word of a line green or red
if [ -n "$DUSK_COMMANDS_FILE" ]; then
    ( mkdir -p "$(dirname "$DUSK_COMMANDS_FILE")" 2>/dev/null; compgen -c | sort -u > "$DUSK_COMMANDS_FILE" 2>/dev/null & )
fi

# The dawn command. dawn file.cs edits the file in place of the terminal, Dawn's editor taking over the pane until
# Ctrl+Q, dawn -open path hands a file or a folder to the Dawn that's running, dawn -window path opens a new Dawn
# window on it, and dawn on its own opens the folder the shell is in. The terminal does all of it, the shell only says
# what was asked for with a private OSC (1717) and goes straight back to its prompt. The path goes as Windows has it,
# WSL's through wslpath (its folder, wslpath won't do a file that isn't there yet) and Git Bash's through cygpath.
dawn() {
    local mode=inline target= word folder name
    for word in "$@"; do
        case "$word" in
            -w|-window|--window) mode=window ;;
            -o|-open|--open) mode=open ;;
            *) target="$word" ;;
        esac
    done

    if [ -z "$target" ]; then
        [ "$mode" = inline ] && mode=open
        target="$PWD"
    fi
    case "$target" in
        /*) ;;
        *) target="$PWD/$target" ;;
    esac

    if command -v wslpath >/dev/null 2>&1; then
        if [ -d "$target" ]; then
            target="$(wslpath -w "$target")"
        else
            folder="$(dirname "$target")"; name="$(basename "$target")"
            target="$(wslpath -w "$folder")\\$name"
        fi
    elif command -v cygpath >/dev/null 2>&1; then
        target="$(cygpath -w "$target")"
    fi

    printf '\e]1717;dawn;%s;%s\a' "$mode" "$target"
}

# The tui command. tui on its own says which programs with a screen of their own there are and how to start them, tui
# name starts one in a tab of its own, with -split or -down in a split instead. Nobody found them in the palette.
tui() {
    local where=tab name= word
    for word in "$@"; do
        case "$word" in
            -split|--split) where=split ;;
            -down|--down) where=down ;;
            *) name="$word" ;;
        esac
    done

    if [ -z "$name" ]; then
        echo 'The programs with a screen of their own, each in a tab unless told -split or -down:'
        echo '  tui files       a file picker, Ctrl+Shift+O'
        echo '  tui git         the status and the log of the repository, Ctrl+Shift+G'
        echo '  tui processes   what is running, and ending it'
        echo '  tui settings    the terminal'\''s settings'
        echo '  tui repl        HIDL with a list of names, in a split'
        echo 'Your own go in the tui folder of Dusk'\''s home, Ctrl+Shift+P and "tui" lists the lot.'
        return
    fi

    printf '\e]1717;tui;%s;%s\a' "$where" "$name"
}
