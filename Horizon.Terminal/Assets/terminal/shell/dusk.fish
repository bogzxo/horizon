# Shell integration for fish, given to the shell by the terminal (Dusk, Hex's terminal page) with --init-command as it
# starts, after the user's own config and never written into it. It says where the prompt starts and ends, where a
# command's output starts and how the command ended (FinalTerm's OSC 133, A B C D) and which folder the shell is in
# (OSC 7), the same as dusk.bash.

if not set -q __dusk_integrated
    set -g __dusk_integrated 1

    function __dusk_folder --on-event fish_prompt
        printf '\e]7;file://%s%s\a' (hostname) (string replace -a ' ' '%20' -- $PWD)
    end
end

# fish 4 says the marks of OSC 133 itself, an older one is given them
if not set -q __dusk_marked; and test (string split . -- $version)[1] -lt 4
    set -g __dusk_marked 1

    function __dusk_prompt_start --on-event fish_prompt
        printf '\e]133;A\a'
    end

    function __dusk_preexec --on-event fish_preexec
        printf '\e]133;C\a'
    end

    function __dusk_postexec --on-event fish_postexec
        printf '\e]133;D;%s\a' $status
    end
end

# The end of the prompt (B), after whatever the prompt is (theirs, a theme's or fish's own), which fish 4 doesn't say
if not functions -q __dusk_their_prompt
    functions -q fish_prompt; and functions -c fish_prompt __dusk_their_prompt
    function fish_prompt
        functions -q __dusk_their_prompt; and __dusk_their_prompt
        printf '\e]133;B\a'
    end
end

# What commands the shell has (its builtins and functions now, every program on its PATH a moment later in the
# background), for the terminal to colour the first word of a line green or red
if set -q DUSK_COMMANDS_FILE
    mkdir -p (dirname $DUSK_COMMANDS_FILE) 2>/dev/null
    begin
        builtin -n
        functions -an
    end > $DUSK_COMMANDS_FILE.fish 2>/dev/null
    command sh -c 'IFS=:; for d in $PATH; do ls "$d" 2>/dev/null; done | cat "$1.fish" - | sort -u > "$1.all" && mv -f "$1.all" "$1"; rm -f "$1.fish"' sh $DUSK_COMMANDS_FILE &
    disown 2>/dev/null
end

# The dawn command, as dusk.bash has it
function dawn
    set -l mode inline
    set -l target
    for word in $argv
        switch $word
            case -w -window --window
                set mode window
            case -o -open --open
                set mode open
            case '*'
                set target $word
        end
    end

    if test -z "$target"
        test $mode = inline; and set mode open
        set target $PWD
    end
    string match -q '/*' -- $target; or set target $PWD/$target

    printf '\e]1717;dawn;%s;%s\a' $mode $target
end

# The tui command, as dusk.bash has it
function tui
    set -l where tab
    set -l name
    for word in $argv
        switch $word
            case -split --split
                set where split
            case -down --down
                set where down
            case '*'
                set name $word
        end
    end

    if test -z "$name"
        echo 'The programs with a screen of their own, each in a tab unless told -split or -down:'
        echo '  tui files       a file picker, Ctrl+Shift+O'
        echo '  tui git         the status and the log of the repository, Ctrl+Shift+G'
        echo '  tui processes   what is running, and ending it'
        echo "  tui settings    the terminal's settings"
        echo '  tui repl        HIDL with a list of names, in a split'
        echo "Your own go in the tui folder of Dusk's home, Ctrl+Shift+P and \"tui\" lists the lot."
        return
    end

    printf '\e]1717;tui;%s;%s\a' $where $name
end
