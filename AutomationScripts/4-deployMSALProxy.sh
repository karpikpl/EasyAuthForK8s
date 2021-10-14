#!/bin/sh -x

# Every file has one dot instead of two because we are calling main.sh, so we access the "current" directory which is where main.sh is located.

echo "BEGIN @ $(date +"%T"): Deploy MSAL Proxy..."

# kubectl apply -f msal-net-proxy.yaml

echo "BEGIN @ $(date +"%T"): Calling Helm..."
echo ""

helm install --set secret.azureadtenantid=$AZURE_TENANT_ID --set secret.azureadclientid=$CLIENT_ID --set secret.azureclientsecret=$CLIENT_SECRET msal-proxy ./charts/msal-proxy

echo ""
echo "COMPLETE @ $(date +"%T"): Calling Helm"

kubectl get svc,deploy,pod
  
INPUT_STRING=false
n=50
while [ "$INPUT_STRING" != "true" ]
do
  echo ""
  kubectl get svc,deploy,pod
  echo ""
  INPUT_STRING=$(kubectl get svc,deploy,pod -o=jsonpath='{.items[3].status.containerStatuses[0].ready}')
  sleep 10
  if [ "$n" == "0" ]; then
    echo "ERROR. INFINITE LOOP in 4-deployMSALProxy.sh."
    exit 1
  fi
  n=$((n-1))
done

echo "COMPLETE @ $(date +"%T"): Deploy MSAL Proxy"