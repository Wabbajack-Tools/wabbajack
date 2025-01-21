﻿using ReactiveUI;
using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media;

namespace Wabbajack;

/// <summary>
/// Interaction logic for DetailImageView.xaml
/// </summary>
public partial class DetailImageView : UserControlRx<ViewModel>
{
    public ImageSource Image
    {
        get => (ImageSource)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(nameof(Image), typeof(ImageSource), typeof(DetailImageView),
         new FrameworkPropertyMetadata(default(ImageSource), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, WireNotifyPropertyChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(DetailImageView),
         new FrameworkPropertyMetadata(default(string), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, WireNotifyPropertyChanged));

    public double TitleFontSize
    {
        get => (double)GetValue(TitleFontSizeProperty);
        set => SetValue(TitleFontSizeProperty, value);
    }
    public static readonly DependencyProperty TitleFontSizeProperty = DependencyProperty.Register(nameof(TitleFontSize), typeof(double), typeof(DetailImageView), new FrameworkPropertyMetadata(default(double), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, WireNotifyPropertyChanged));

    public string Author
    {
        get => (string)GetValue(AuthorProperty);
        set => SetValue(AuthorProperty, value);
    }
    public static readonly DependencyProperty AuthorProperty = DependencyProperty.Register(nameof(Author), typeof(string), typeof(DetailImageView),
         new FrameworkPropertyMetadata(default(string), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, WireNotifyPropertyChanged));
    public double AuthorFontSize
    {
        get => (double)GetValue(AuthorFontSizeProperty);
        set => SetValue(AuthorFontSizeProperty, value);
    }
    public static readonly DependencyProperty AuthorFontSizeProperty = DependencyProperty.Register(nameof(AuthorFontSize), typeof(double), typeof(DetailImageView), new FrameworkPropertyMetadata(default(double), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, WireNotifyPropertyChanged));
    public Version? Version
    {
        get => (Version?)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(Version), typeof(DetailImageView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, WireNotifyPropertyChanged));


    public DetailImageView()
    {
        InitializeComponent();

        this.WhenActivated(dispose =>
        {
            // Update textboxes
            var authorVisible = this.WhenAny(x => x.Author)
                .Select(x => string.IsNullOrWhiteSpace(x) ? Visibility.Collapsed : Visibility.Visible)
                .Replay(1)
                .RefCount();
            authorVisible
                .BindToStrict(this, x => x.AuthorTextBlock.Visibility)
                .DisposeWith(dispose);
            this.WhenAny(x => x.Author)
                .BindToStrict(this, x => x.AuthorTextRun.Text)
                .DisposeWith(dispose);

            var titleVisible = this.WhenAny(x => x.Title)
                .Select(x => string.IsNullOrWhiteSpace(x) ? Visibility.Collapsed : Visibility.Visible)
                .Replay(1)
                .RefCount();
            titleVisible
                .BindToStrict(this, x => x.TitleTextBlock.Visibility)
                .DisposeWith(dispose);
            this.WhenAny(x => x.Title)
                .BindToStrict(this, x => x.TitleTextBlock.Text)
                .DisposeWith(dispose);

            /*
            var versionVisible = this.WhenAny(x => x.Version)
                .Select(x => x?.ToString() ?? string.Empty)
                .Select(x => string.IsNullOrWhiteSpace(x) ? Visibility.Hidden : Visibility.Visible)
                .Replay(1)
                .RefCount();
            versionVisible
                .BindToStrict(this, x => x.VersionTextRun.Visibility)
                .DisposeWith(dispose);
            */
            this.WhenAny(x => x.Version)
                .Select(x => x != null ? x.ToString() : string.Empty)
                .BindToStrict(this, x => x.VersionTextRun.Text)
                .DisposeWith(dispose);

            this.WhenAny(x => x.Version)
                .Subscribe(x => VersionPrefixRun.Text = x != null ? "version" : string.Empty)
                .DisposeWith(dispose);

            this.WhenAny(x => x.Image)
                .Select(f => f)
                .BindToStrict(this, x => x.ModlistImage.Source)
                .DisposeWith(dispose);
            this.WhenAny(x => x.Image)
                .Select(img => img == null ? Visibility.Hidden : Visibility.Visible)
                .BindToStrict(this, x => x.ModlistImage.Visibility)
                .DisposeWith(dispose);

            this.WhenAny(x => x.TitleFontSize)
                .BindToStrict(this, x => x.TitleTextBlock.FontSize)
                .DisposeWith(dispose);
            this.WhenAny(x => x.AuthorFontSize)
                .BindToStrict(this, x => x.AuthorTextBlock.FontSize)
                .DisposeWith(dispose);
        });
    }
}
