import re
with open('AxePower.App/Views/BatteryView.xaml', 'r', encoding='utf-8') as f:
    text = f.read()

# Replace Grid with WrapPanel
text = re.sub(r'<Grid Grid\.Row="1" Margin="0,0,0,16">\s*<Grid\.ColumnDefinitions>.*?</Grid\.ColumnDefinitions>', '<WrapPanel Grid.Row="1" Margin="0,0,0,16">', text, flags=re.DOTALL)

# Find the closing </Grid> for Top Cards (before Bottom Area)
text = text.replace('            </Grid>\n\n            <!-- Bottom Area -->', '            </WrapPanel>\n\n            <!-- Bottom Area -->')

# Replace Grid.Column="x" with Width="220" Margin="0,0,16,16"
def replacer(m):
    return '<Border Width="220" Margin="0,0,16,16" ' + m.group(1)

text = re.sub(r'<Border Grid\.Column="\d+" (Background="\{StaticResource ElevatedCardBackgroundBrush\}")', replacer, text)

# Wrap the main Grid in a ScrollViewer
text = text.replace('<Grid Grid.Row="0" Margin="40,32,40,24">', '<ScrollViewer Grid.Row="0" VerticalScrollBarVisibility="Auto">\n            <Grid Margin="40,32,40,24">')

text = text.replace('        </Grid>\n\n        <!-- Bottom Status Region -->', '            </Grid>\n        </ScrollViewer>\n\n        <!-- Bottom Status Region -->')

with open('AxePower.App/Views/BatteryView.xaml', 'w', encoding='utf-8') as f:
    f.write(text)
print('Done!')
