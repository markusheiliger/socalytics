import { createTheme } from '@mui/material/styles';

const theme = createTheme({
  palette: {
    primary: { main: '#0b5cad' },
    secondary: { main: '#2e7d32' },
  },
  typography: {
    h1: { fontSize: '2rem', fontWeight: 600 },
  },
});

export default theme;
