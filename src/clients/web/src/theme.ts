import { createTheme } from '@mui/material/styles';

const theme = createTheme({
  palette: {
    primary: { main: '#0b5cad' },
    secondary: { main: '#2e7d32' },
  },
  typography: {
    h1: { fontSize: '2rem', fontWeight: 600 },
  },
  components: {
    MuiButtonBase: {
      styleOverrides: {
        root: { '&.Mui-focusVisible': { outline: '2px solid #0b5cad', outlineOffset: 2 } },
      },
    },
    MuiOutlinedInput: {
      styleOverrides: {
        root: { '&.Mui-focused': { outline: '2px solid #0b5cad', outlineOffset: 2 } },
      },
    },
  },
});

export default theme;
