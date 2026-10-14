import Box from '@mui/material/Box';
import { Outlet } from 'react-router';
import { ConnectivityIndicator } from './ConnectivityIndicator';
import { Navigation } from './Navigation';

export function ShellHeader() {
  return (
    <Box component="header" sx={{ borderBottom: 1, borderColor: 'divider', px: 2, py: 1 }}>
      <Navigation />
      <ConnectivityIndicator />
    </Box>
  );
}

/** Frame of every signed-in view: navigation and connectivity state around the view's own main landmark. */
export function AppLayout() {
  return (
    <>
      <ShellHeader />
      <Outlet />
    </>
  );
}
