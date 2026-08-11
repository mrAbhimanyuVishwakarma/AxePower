import re

with open('AxePower.App/Views/DashboardView.xaml', 'r', encoding='utf-8') as f:
    content = f.read()

def replacer(match):
    margin = match.group(1) or ""
    icon_text = match.group(2)
    brush = match.group(3)
    text = match.group(4)
    return f'''<Grid{margin}>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="26"/>
                                    <ColumnDefinition Width="*"/>
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="{icon_text}" FontFamily="Segoe MDL2 Assets" Foreground="{{StaticResource {brush}}}" FontSize="14" VerticalAlignment="Center"/>
                                <TextBlock Grid.Column="1" Text="{text}" Foreground="{{StaticResource SecondaryTextBrush}}" FontSize="13" VerticalAlignment="Center" TextWrapping="Wrap"/>
                            </Grid>'''

pattern = r'<StackPanel Orientation="Horizontal"( Margin="[^"]*")?>\s*<TextBlock Text="([^"]*)" FontFamily="Segoe MDL2 Assets" Foreground="\{StaticResource ([^\}]+)\}" FontSize="14" Margin="0,0,12,0" VerticalAlignment="Center"/>\s*<TextBlock Text="([^"]*)" Foreground="\{StaticResource SecondaryTextBrush\}" FontSize="13" VerticalAlignment="Center"\s*(?:TextWrapping="Wrap"\s*)?/>\s*</StackPanel>'

new_content = re.sub(pattern, replacer, content)

with open('AxePower.App/Views/DashboardView.xaml', 'w', encoding='utf-8') as f:
    f.write(new_content)

print("Done")
