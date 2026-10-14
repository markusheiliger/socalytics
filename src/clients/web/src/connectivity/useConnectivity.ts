import { useContext } from 'react';
import { ConnectivityContext, type ConnectivityState } from './connectivityContext';

export function useConnectivity(): ConnectivityState {
  return useContext(ConnectivityContext);
}
