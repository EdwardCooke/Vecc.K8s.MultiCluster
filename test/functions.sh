#!/bin/bash

# usage:
#   use_context <cluster id>
#
#   returns an exit code of 0 if succesfull, non zero if not.
# remarks:
#   use 1 for the first test cluster
#   use 2 for the second test cluster
use_context() {
    kubectl config use-context "kind-test$1"
}

# usage:
#   wait_for_resource <resourcetype> <for> <label selector> <timeout>
#
#   returns an exit code of 1 if the timeout elapsed before the resource entered the desired state
#   returns an exit code of 0 if the resource entered the desired state within the timeout
#
# remarks:
#   If timeout is not specified, it defaults to 30 seconds
#
# example:
# * to wait for pods with a label sector of "app.kubernetes.io/component=controller" to become ready for 10 seconds
#   wait_for_resource pod condition=ready app.kubernetes.io/component=controller 10
wait_for_resource() {
    RESOURCE=$1
    WAITFOR=$2
    SELECTOR=$3
    TIMEOUT=${4:-30}
    STARTTIME=$(date +%s)
    ESTATE=$-; ESTATE=$(get_sete "$ESTATE")

    set +e

    echo "Waiting for $RESOURCE with selector $SELECTOR for up to $TIMEOUT seconds to be $WAITFOR"
    LASTEXITCODE=1

    while [ $LASTEXITCODE != 0 ]
    do
        kubectl wait --for="$WAITFOR" "$RESOURCE" --selector="$SELECTOR" --timeout=1s 1> /dev/null 2> /dev/null
        LASTEXITCODE=$?
        if [ $LASTEXITCODE != 0 ]
        then
            # check to see if we timed out
            NOW=$(date +%s)
            (( (NOW-STARTTIME) > TIMEOUT )) && echo "Timeout expired" && set_sete "$ESTATE" && return 1
            echo "Timeout not expired, waiting for another second."
            sleep 1
        fi
    done

    set_sete "$ESTATE"

    return 0
}

wait_for_resource_missing() {
    RESOURCE=$1
    SELECTOR=$2
    TIMEOUT=${3:-30}
    STARTTIME=$(date +%s)
    ESTATE=$-; ESTATE=$(get_sete "$ESTATE")

    set +e

    echo "Waiting for $RESOURCE with selector $SELECTOR for up to $TIMEOUT seconds to be $WAITFOR"
    LASTEXITCODE=1

    while [ $LASTEXITCODE != 0 ]
    do
        kubectl get "$RESOURCE" --selector="$SELECTOR" 2>&1 | grep -c "No resources" 1> /dev/null 2> /dev/null
        LASTEXITCODE=$?
        if [ $LASTEXITCODE != 0 ]
        then
            # check to see if we timed out
            NOW=$(date +%s)
            (( (NOW-STARTTIME) > TIMEOUT )) && echo "Timeout expired" && set_sete "$ESTATE" && return 1
            echo "Timeout not expired, waiting for another second."
            sleep 1
        fi
    done

    set_sete "$ESTATE"

    return 0
}
# usage:
#   set_namespace <namespacename>
#
#   returns an exit code of 0 if succesfull, non zero if not.
set_namespace() {
    kubectl config set-context --current --namespace "$1"

    return $?
}

# usage:
#   get_ip <cluster id> <hostname>
#
#   returns the hostname, written to stdout with no new line
#
# example:
#   Get the resulting IP of the hostname only-in-cluster-test1.test1 from the first test cluster
#      get_ip 1 only-in-cluster-test1.test1
get_ip() {
    IP=$(dig "$2" @127.0.0.1${1} +short +tcp || true)

    echo -n "$IP"
}

# usage:
#  wait_for_ingress <ingress name> <timeout>
#
#   returns an exit code of 1 if the timeout elapsed before the ingress is recognized by NGINX
#   returns an exit code of 0 if the ingress is recognized by NGINX within the timeout
#
# remarks:
#   If timeout is not specified, it defaults to 90 seconds
wait_for_ingress() {
    INGRESS=$1
    TIMEOUT=${2:-90}
    STARTTIME=$(date +%s)
    LASTEXITCODE=1
    ESTATE=$-; ESTATE=$(get_sete "$ESTATE")

    set +e

    while [ $LASTEXITCODE != 0 ]
    do
        IP=$(kubectl get ingress "$INGRESS" -o jsonpath="{.status.loadBalancer.ingress[0].ip}")
        LASTEXITCODE=$?
        if [[ "$LASTEXITCODE" != "0" || "$IP" == "" ]]
        then
            # check to see if we timed out
            NOW=$(date +%s)
            if (( (NOW-STARTTIME) > TIMEOUT ))
            then
                echo "Timeout expired"
                set_sete "$ESTATE"
                return 1
            fi
            echo "Timeout not expired, waiting for another second."
            sleep 1
            LASTEXITCODE=1
        fi
    done
    NOW=$(date +%s)
    (( ELAPSED=NOW-STARTTIME )) || true
    echo "It took $ELAPSED seconds for the ingress to be configured"
    set_sete "$ESTATE"

    return 0
}

get_sete() {
    echo "$1" | grep -o e 1> /dev/null 2> /dev/null && echo -n "1" || echo -n "0"
}

set_sete() {
    if [ "$1" == "1" ]
    then
        set -e
    else
        set +e
    fi
}

R='\033[31m'   #'31' is Red's ANSI color code
G='\033[32m'   #'32' is Green's ANSI color code
# shellcheck disable=SC2034
Y='\033[33m'   #'33' is Yellow's ANSI color code
# shellcheck disable=SC2034
B='\033[34m'   #'34' is Blue's ANSI color code

NOCOLOR='\033[0m'

echo_color() {
    echo -e "$1${NOCOLOR}"
}

echo_color_error() {
    echo -e "$1${NOCOLOR}" >&2
}

spinner_setup() {
        STDOUTFILE="$1/setup-stdout.txt"
        STDERRFILE="$1/setup-stderr.txt"

        spinner_wait "Setting up" "
        . ./test.sh
        echo 'Setup started at $(date)' > $STDOUTFILE
        setup 1>> $STDOUTFILE 2> $STDERRFILE
        RET=\$?
        echo 'Setup completed at $(date)' >> $STDOUTFILE
        exit \$RET"

        return $?
}

spinner_assert() {
        STDOUTFILE="$1/assert-stdout.txt"
        STDERRFILE="$1/assert-stderr.txt"

        spinner_wait "Asserting" "
        . ./test.sh
        echo 'Assert started at $(date)' > $STDOUTFILE
        assert 1>> $STDOUTFILE 2> $STDERRFILE
        RET=\$?
        echo 'Assert completed at $(date)' >> $STDOUTFILE
        exit \$RET"

        return $?
}

spinner_cleanup() {
        STDOUTFILE="$1/cleanup-stdout.txt"
        STDERRFILE="$1/cleanup-stderr.txt"

        spinner_wait "Cleaning up" "
        . ./test.sh
        echo 'Cleanup started at $(date)' > $STDOUTFILE
        cleanup 1>> $STDOUTFILE 2> $STDERRFILE
        RET=\$?
        echo 'Cleanup completed at $(date)' >> $STDOUTFILE
        exit \$RET"

        return $?
}

spinner_wait() {
    (
        TEXT=$1
        shift

        # we use eval like this because we want to execute the bash snippets that get passed in in the background. eval used in this way is required for that.
        # shellcheck disable=SC2294
        eval "$@" &

        PID=$!
        SPINNER=("${G}⣷${NOCOLOR}" "${G}⣯${NOCOLOR}" "${G}⣟${NOCOLOR}" "${G}⡿${NOCOLOR}" "${B}⢿${NOCOLOR}" "${B}⣻${NOCOLOR}" "${B}⣽${NOCOLOR}" "${B}⣾${NOCOLOR}")
        POS=0

        [ "$IS_CI" == "1" ] && echo -e "$TEXT"

        while (kill -0 $PID 1> /dev/null 2> /dev/null)
        do
            (( POS++ ))
            [ $POS == 8 ] && POS=0
            [ "$IS_CI" != "1" ] && echo -en "\r${SPINNER[$POS]} $TEXT"
            sleep .1
        done
        wait $PID
        RETVAL=$?

        [ $RETVAL == 0 ] && CHAR="${G}✓${NOCOLOR}" || CHAR="${R}✗${NOCOLOR}"
        echo -e "\r${CHAR} $TEXT"
        return $RETVAL
    )
}

spinner_test() {
    OUTPUTDIRECTORY=$1
    SETUPRESULT=0
    ASSERTRESULT=0
    CLEANUPRESULT=0
    RESULT=0
    mkdir -p "$OUTPUTDIRECTORY"
    spinner_setup "$OUTPUTDIRECTORY"
    SETUPRESULT=$?
    if [ $SETUPRESULT == 0 ]
    then
        spinner_assert "$OUTPUTDIRECTORY"
        ASSERTRESULT=$?
    fi
    spinner_cleanup "$OUTPUTDIRECTORY"
    CLEANUPRESULT=$?
    (( RESULT = SETUPRESULT + ASSERTRESULT + CLEANUPRESULT )) || true

    return $RESULT
}
