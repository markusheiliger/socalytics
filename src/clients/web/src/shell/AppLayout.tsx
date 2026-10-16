import Box from '@mui/material/Box';
import { Outlet } from 'react-router';
import { ConnectivityIndicator } from './ConnectivityIndicator';
import { Navigation } from './Navigation';
import { UpdateNotice } from './UpdateNotice';
import { UploadIndicator } from './UploadIndicator';

export function ShellHeader() {
  return (
    <Box component="header" sx={{ borderBottom: 1, borderColor: 'divider', px: 2, py: 1 }}>
      <Navigation />
      <ConnectivityIndicator />
      <UploadIndicator />
      <UpdateNotice />
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
