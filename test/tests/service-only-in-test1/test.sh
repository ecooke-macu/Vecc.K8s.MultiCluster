#!/bin/bash

. ../../functions.sh

setup() {
    set -e
    use_context 1
    kubectl apply -f test.yaml

    sleep 1
    set_namespace only-in-test1
    wait_for_resource pod condition=ready app=nginx
    
    return $?
}

assert() {
    RESULT=0
    # do this 100 times
    COUNT=0
    while
        let COUNT+=1
        ACTUAL=`get_ip 1 only-in-test1.test1`
        EXPECTED=$CLUSTER1IP
        [ "$ACTUAL" != "$EXPECTED" ] && echo "Cluster 1 ip mismatch" && RESULT=1 && break

        ACTUAL=`get_ip 2 only-in-test1.test1`
        EXPECTED=$CLUSTER2IP
        [ "$ACTUAL" != "$EXPECTED" ] && echo "Cluster 2 ip mismatch" && RESULT=1 && break
    do (( $COUNT < 100 ))
    done
    return $RESULT
}

cleanup() {
    kubectl delete namespace only-in-test1
    return $?
}