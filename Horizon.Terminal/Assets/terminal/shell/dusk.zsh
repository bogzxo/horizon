# Shell integration for zsh, read by zsh/.zshrc after the user's own (see zsh/.zshenv for how). It says where the
# prompt starts and ends, where a command's output starts and how the command ended (FinalTerm's OSC 133, A B C D) and
# which folder the shell is in (OSC 7), the same as dusk.bash.

if [[ -z "$__dusk_integrated" ]]; then
    __dusk_integrated=1
    __dusk_ran=

    # First of the precmd functions, before any of theirs changes $?
    __dusk_status() {
        local code=$?
        [[ -n "$__dusk_ran" ]] && printf '\e]133;D;%s\a' "$code"
        __dusk_ran=
    }

    # Last of them, after a theme has made its prompt: the folder, and the marks round the prompt, put back on every
    # time as a theme may make PS1 anew
    __dusk_prompt() {
        printf '\e]7;file://%s%s\a' "${HOST:-localhost}" "${PWD// /%20}"
        [[ "$PS1" == *$'\e]133;A'* ]] || PS1="%{"$'\e]133;A\a'"%}$PS1"
        [[ "$PS1" == *$'\e]133;B'* ]] || PS1="$PS1%{"$'\e]133;B\a'"%}"
    }

    __dusk_preexec() {
        __dusk_ran=1
        printf '\e]133;C\a'
    }

    autoload -Uz add-zsh-hook
    precmd_functions=(__dusk_status ${precmd_functions[@]})
    add-zsh-hook precmd __dusk_prompt
    add-zsh-hook preexec __dusk_preexec
fi

# What commands the shell has (every program on its PATH, its builtins, functions and aliases), written in the
# background so the prompt isn't kept waiting, for the terminal to colour the first word of a line green or red
if [[ -n "$DUSK_COMMANDS_FILE" ]]; then
    # Into a file of its own and then moved over, so the terminal never reads it half written
    { mkdir -p "${DUSK_COMMANDS_FILE:h}" 2>/dev/null; print -rl -- ${(k)commands} ${(k)builtins} ${(k)functions} ${(k)aliases} | sort -u > "$DUSK_COMMANDS_FILE.$$" 2>/dev/null && mv -f "$DUSK_COMMANDS_FILE.$$" "$DUSK_COMMANDS_FILE" } &!
fi

# The dawn command, as dusk.bash has it: dawn file.cs edits the file in place of the terminal until Ctrl+Q, dawn -open
# path hands a file or a folder to the Dawn that's running, dawn -window path opens a new Dawn window on it, and dawn on
# its own opens the folder the shell is in
dawn() {
    local mode=inline target= word
    for word in "$@"; do
        case "$word" in
            -w|-window|--window) mode=window ;;
            -o|-open|--open) mode=open ;;
            *) target="$word" ;;
        esac
    done

    if [[ -z "$target" ]]; then
        [[ "$mode" == inline ]] && mode=open
        target="$PWD"
    fi
    [[ "$target" == /* ]] || target="$PWD/$target"

    printf '\e]1717;dawn;%s;%s\a' "$mode" "$target"
}

# The tui command, as dusk.bash has it
tui() {
    local where=tab name= word
    for word in "$@"; do
        case "$word" in
            -split|--split) where=split ;;
            -down|--down) where=down ;;
            *) name="$word" ;;
        esac
    done

    if [[ -z "$name" ]]; then
        print 'The programs with a screen of their own, each in a tab unless told -split or -down:'
        print '  tui files       a file picker, Ctrl+Shift+O'
        print '  tui git         the status and the log of the repository, Ctrl+Shift+G'
        print '  tui processes   what is running, and ending it'
        print '  tui settings    the terminal'\''s settings'
        print '  tui repl        HIDL with a list of names, in a split'
        print 'Your own go in the tui folder of Dusk'\''s home, Ctrl+Shift+P and "tui" lists the lot.'
        return
    fi

    printf '\e]1717;tui;%s;%s\a' "$where" "$name"
}
